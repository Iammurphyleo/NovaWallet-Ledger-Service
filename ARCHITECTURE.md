# NovaWallet Ledger Service — Architecture & Design Decisions

## Table of Contents

1. [System Overview](#system-overview)
2. [Request Lifecycle (End-to-End)](#request-lifecycle-end-to-end)
3. [Layer Structure](#layer-structure)
4. [Concurrency Strategy](#concurrency-strategy)
5. [Idempotency Design](#idempotency-design)
6. [Daily Limit (WAT Timezone)](#daily-limit-wat-timezone)
7. [Audit Log & Outbox Pattern](#audit-log--outbox-pattern)
8. [Authentication & Ownership](#authentication--ownership)
9. [Error Handling (RFC 7807)](#error-handling-rfc-7807)
10. [Controller Design — Why No Logic](#controller-design--why-no-logic)
11. [Database Schema](#database-schema)
12. [Testing Strategy](#testing-strategy)
13. [Alternatives Considered](#alternatives-considered)

---

## System Overview

NovaWallet is a production-grade wallet ledger service. Every monetary value is stored and processed as **kobo** (integer `long` / PostgreSQL `BIGINT`). There is no floating-point arithmetic anywhere in the codebase — this eliminates rounding errors that would otherwise be a compliance risk in a financial system.

```
Client → [JWT Auth] → [Rate Limiter] → Controller → WalletService → PostgreSQL
                                                            ↓
                                                      AuditLog + Outbox (same transaction)
```

---

## Request Lifecycle (End-to-End)

### Example: POST /api/v1/transfers

```
1. Request arrives
   └─ CorrelationIdMiddleware
      - Reads X-Correlation-Id header (or generates a new UUID if absent)
      - Stores it in HttpContext.Items
      - Echoes it back in the response header
      - Opens a structured log scope so every log line in this request carries CorrelationId

2. ExceptionHandlingMiddleware wraps the rest of the pipeline
   - Catches domain exceptions and maps them to RFC 7807 ProblemDetails
   - Logs 4xx at Warning, 5xx at Error

3. Rate limiter (ASP.NET Core built-in)
   - Fixed-window: 10 requests/minute on /transfers
   - Rejects with HTTP 429 if exceeded

4. [Authorize] filter
   - Validates the JWT (signature, issuer, audience, expiry)
   - Populates HttpContext.User

5. [RequireIdempotencyKey] action filter
   - Reads Idempotency-Key header
   - Short-circuits with HTTP 400 ProblemDetails if missing or > 255 chars
   - No controller code needed for this guard

6. [ApiController] model binding + automatic model state validation
   - Binds and validates TransferRequest via FluentValidation (registered as MVC filter)
   - Returns HTTP 400 ValidationProblemDetails automatically if invalid

7. WalletsController.Transfer — pure dispatch
   - Reads correlation ID from HttpContext.Items
   - Calls WalletService.TransferAsync(request, key, subject, correlationId, ct)
   - Returns Ok(result)

8. WalletService.TransferAsync

   Phase 1 — Claim idempotency key (outside main transaction):
   ├─ Hash the request body (SHA-256) to detect payload mismatches
   ├─ INSERT INTO idempotency_records ... ON CONFLICT (key) DO NOTHING
   │  - Returns 1 row if we won the race, 0 if key already existed
   │  - If 0: re-read and replay completed response, or reject if in-flight
   └─ We now own a PROCESSING record — safe to execute the transfer

   Phase 2 — Transfer + completion in a single atomic transaction:
   ├─ BEGIN TRANSACTION (READ COMMITTED isolation)
   ├─ SELECT * FROM wallets WHERE id IN (src, dst) ORDER BY id FOR UPDATE
   │  - Locks both rows; ordering by UUID prevents deadlock
   ├─ Read daily outbound sum INSIDE the lock (prevents concurrent limit bypass)
   ├─ source.Debit(amount)   ← domain method, throws InsufficientFundsException if < 0
   ├─ destination.Credit(amount)
   ├─ INSERT wallet_transactions (src debit + dst credit)
   ├─ INSERT audit_logs (TransferSent + TransferReceived)
   ├─ INSERT outbox_messages (TransferCompleted event payload)
   ├─ UPDATE idempotency_records SET status='Completed' WHERE key=?  ← INSIDE same txn
   ├─ SaveChanges()
   └─ COMMIT

   On any failure:
   ├─ ROLLBACK (nothing committed — wallets unchanged)
   └─ DELETE FROM idempotency_records WHERE key=? AND status='Processing'
      (removes the claim so the client can retry)

9. Response → CorrelationIdMiddleware echoes X-Correlation-Id header
```

---

## Layer Structure

```
NovaWallet.Domain          Pure business rules. No framework dependencies.
  Entities/               Wallet, WalletTransaction, AuditLog, IdempotencyRecord, OutboxMessage
  Exceptions/             Domain-specific exceptions (InsufficientFundsException, etc.)
  Interfaces/             IWalletRepository, IIdempotencyRepository, etc. (contracts only)
  Enums/

NovaWallet.Application     Orchestration. Depends on Domain only.
  Services/WalletService  All use-case logic: validation dispatch, concurrency, idempotency
  DTOs/                   Request/response shapes (no domain entities exposed)
  Validators/             FluentValidation rules for all incoming requests
  Interfaces/IWalletService
  Settings/WalletSettings  DailyOutboundLimitKobo (configurable)

NovaWallet.Infrastructure  I/O implementations. Depends on Application + Domain.
  Data/NovaWalletDbContext EF Core context
  Repositories/           WalletRepository (FOR UPDATE locking), IdempotencyRepository, etc.
  Migrations/             EF Core generated migrations
  Extensions/             AddInfrastructure() DI registration

NovaWallet.Presentation    HTTP surface. Depends on Application only.
  Controllers/            Thin dispatch — no business logic
  Middleware/             CorrelationIdMiddleware, ExceptionHandlingMiddleware
  Filters/                RequireIdempotencyKeyAttribute (action filter)
  DTOs/                   StatementQuery (query param DTO with Range validation)
  Program.cs              Composition root
```

**Why Clean Architecture here?**
The domain and application layers have zero HTTP, EF Core, or framework dependencies. This means `WalletService` and all business rules can be unit-tested with plain mocks — no web server, no database, no Docker. The 38 unit tests reflect this: they run in under 2 seconds.

---

## Concurrency Strategy

**Decision: Pessimistic locking (SELECT FOR UPDATE)**

When two requests try to debit the same wallet simultaneously, one of them must wait. We use PostgreSQL row-level locking:

```sql
SELECT * FROM wallets WHERE id IN ($1, $2) ORDER BY id FOR UPDATE
```

The `ORDER BY id` is load-bearing: it ensures that any two concurrent transfers always acquire locks in the same order (ascending UUID). Without it, transfer A→B and transfer B→A could each lock one wallet and deadlock waiting for the other.

**Why pessimistic over optimistic (row version / retry)?**

| | Pessimistic (FOR UPDATE) | Optimistic (row version) |
|---|---|---|
| Concurrency | Serialises conflicting writers | Allows parallel reads, retries on conflict |
| Deadlock | Prevented by ordered acquisition | Not possible (no locks) |
| Retry logic | None needed | Required in application code |
| Invariant safety | Guaranteed — winner holds lock until commit | Risk: retry logic bug could skip the balance check |
| Latency under load | Higher (waiters queue) | Lower until conflicts spike |

For a financial service where the non-negative balance invariant is a hard correctness requirement (not just a performance concern), pessimistic locking is the safer choice. The lock hold time is extremely short — microseconds to milliseconds — so queue depth stays low in practice.

**Isolation level: READ COMMITTED**
We don't need SERIALIZABLE or REPEATABLE READ because row-level `FOR UPDATE` locks prevent the phantom read that matters here (another transaction inserting a row that changes the daily sum). The daily limit query runs inside the locked transaction and therefore sees only committed data for the locked wallets.

---

## Idempotency Design

**Goal:** A client that retries a failed transfer (network timeout, server crash) must get exactly-once money movement regardless of how many times it retries.

**Two-phase protocol:**

```
Phase 1 (outside transaction):
  INSERT INTO idempotency_records (key, request_hash, status='Processing')
  ON CONFLICT (key) DO NOTHING
  → returns 1 if we own the key, 0 if someone else does

Phase 2 (inside transfer transaction):
  ... wallet debits, credits, audit, outbox ...
  UPDATE idempotency_records SET status='Completed', response_body=? WHERE key=?
  COMMIT
```

The critical property: the idempotency record completion happens **atomically with the transfer**. If the process crashes between `COMMIT` and returning the HTTP response, the next retry finds `status='Completed'` and replays the cached response — no second debit.

**What happens on failure?**
If the transfer fails (insufficient funds, daily limit, DB error), we `ROLLBACK` and then `DELETE FROM idempotency_records WHERE key=? AND status='Processing'`. This is safe because the rollback guarantees no money moved. The client can retry with the same key.

**Request hash:**
We hash the full request body (SHA-256). If a client reuses a key with a different payload, it gets HTTP 422 (`IdempotencyKeyConflictException`). This catches client bugs where a key is accidentally reused for a different transfer.

**Stale PROCESSING records:**
If a server crashes mid-processing, the record stays `PROCESSING` forever. After 30 seconds we treat it as stale and allow a new attempt. This is a configurable window — long enough that a slow-but-healthy transfer won't race, short enough that a dead server doesn't block clients for long.

---

## Daily Limit (WAT Timezone)

The daily outbound limit resets at midnight **West Africa Time (WAT)**, which is UTC+1.

```csharp
private static readonly TimeSpan WatOffset = TimeSpan.FromHours(1);

private static DateTime GetTodayWatMidnightUtc()
{
    var watNow = DateTime.UtcNow.Add(WatOffset);
    var watMidnight = watNow.Date;       // 00:00:00 WAT today
    return watMidnight.Subtract(WatOffset); // back to UTC for the DB query
}
```

**Why not `TimeZoneInfo.FindSystemTimeZoneById("Africa/Lagos")`?**

Nigeria (WAT) has **never observed Daylight Saving Time** and has no scheduled changes. Using the full IANA timezone resolution machinery adds a runtime dependency on OS timezone data — which is not guaranteed to exist in a minimal Docker container, and requires different ID strings on Windows (`"W. Central Africa Standard Time"`) vs Linux (`"Africa/Lagos"`). A hardcoded `TimeSpan.FromHours(1)` is simpler, more portable, has no runtime failure mode, and is semantically identical for this use case. The assumption is documented so it's easy to revisit if Nigeria ever observes DST (which has not happened since 1960).

---

## Audit Log & Outbox Pattern

**Audit log** (`audit_logs` table) is append-only — no updates, no deletes. Every significant event (wallet created, credit, debit, transfer sent/received) writes a record with balances before and after, amount, actor, and correlation ID. This provides a complete financial audit trail that is independent of the transactions table.

**Outbox pattern** (`outbox_messages` table): When a transfer completes, a `TransferCompleted` event is written to `outbox_messages` **inside the same transaction**. A `BackgroundService` (`OutboxProcessorService`) polls unprocessed messages and publishes them (to a message broker, webhook, or downstream system). This guarantees **at-least-once delivery** without distributed transactions — the event is only visible after the transfer commits, so there is no risk of publishing an event for a rolled-back transfer.

Both the audit record and the outbox message are written in the same `COMMIT` as the wallet balance changes. There is no window where a transfer commits but its audit/event is missing.

---

## Authentication & Ownership

JWT bearer authentication with a symmetric signing key. The `sub` claim is the customer ID. Every operation on a wallet checks:

```csharp
if (!string.Equals(wallet.CustomerId, callerSubject, StringComparison.OrdinalIgnoreCase))
    throw new WalletAccessDeniedException(wallet.Id);
```

This means a valid JWT for customer A cannot read or debit customer B's wallet. The mock auth endpoint (`POST /api/v1/auth/token`) accepts any `customerId` and issues a signed token — suitable for testing; in production this would be replaced by an identity provider integration.

---

## Error Handling (RFC 7807)

All error responses conform to [RFC 7807](https://www.rfc-editor.org/rfc/rfc7807) (Problem Details for HTTP APIs). `ExceptionHandlingMiddleware` sits at the top of the pipeline and maps every domain exception to a typed `ProblemDetails` response:

| Exception | HTTP Status | type |
|---|---|---|
| `ValidationException` | 400 | `validation-error` |
| `WalletNotFoundException` | 404 | `wallet-not-found` |
| `WalletAlreadyExistsException` | 409 | `wallet-already-exists` |
| `WalletAccessDeniedException` | 403 | `wallet-access-denied` |
| `InsufficientFundsException` | 422 | `insufficient-funds` |
| `DailyLimitExceededException` | 422 | `daily-limit-exceeded` |
| `IdempotencyKeyConflictException` | 422 | `idempotency-key-conflict` |
| `IdempotencyKeyInFlightException` | 409 | `idempotency-key-in-flight` |
| Anything else | 500 | `internal-error` |

Every response includes a `correlationId` extension field for traceability.

---

## Controller Design — Why No Logic

The controller's only job is to translate HTTP concepts (headers, query params, route params) into application service calls and back. Any `if` statement in a controller is a smell.

**Before refactor — two logic leaks:**

```csharp
// Idempotency header guard — returned non-RFC-7807 anonymous object
if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 255)
    return BadRequest(new { error = "..." }); // wrong format

// Pagination bounds — duplicated validation concern
if (page < 1) return BadRequest(new { error = "page must be ≥ 1." });
if (pageSize is < 1 or > 100) return BadRequest(new { error = "..." });
```

**After refactor — controller is pure dispatch:**

```
[RequireIdempotencyKey]  ← ActionFilter handles header validation, returns ProblemDetails
public async Task<IActionResult> Transfer(...)
{
    // key is guaranteed valid if we reach here
    var result = await _walletService.TransferAsync(...);
    return Ok(result);
}

public async Task<IActionResult> GetStatement(Guid walletId, [FromQuery] StatementQuery query, ...)
{
    // [ApiController] validates StatementQuery's [Range] attributes, returns ValidationProblemDetails
    var statement = await _walletService.GetStatementAsync(..., query.Page, query.PageSize, ct);
    return Ok(statement);
}
```

`RequireIdempotencyKeyAttribute` is an `ActionFilterAttribute` in the Presentation layer — it runs before the action, short-circuits with `ProblemDetails` if invalid, and is reusable on any future endpoint that requires an idempotency key.

`StatementQuery` uses `System.ComponentModel.DataAnnotations.Range` attributes. `[ApiController]` automatically validates model state before the action runs and returns a `ValidationProblemDetails` (RFC 7807 compliant) without any controller code needed.

---

## Database Schema

```
wallets
  id            UUID         PK
  customer_id   TEXT         UNIQUE (one wallet per customer)
  balance_kobo  BIGINT       NOT NULL, CHECK (balance_kobo >= 0)
  currency      TEXT         DEFAULT 'NGN'
  created_at    TIMESTAMPTZ
  updated_at    TIMESTAMPTZ

wallet_transactions
  id                  UUID      PK
  wallet_id           UUID      FK → wallets
  reference           TEXT      UNIQUE (generated, human-readable)
  type                TEXT      'Credit' | 'Debit'
  amount_kobo         BIGINT    NOT NULL
  balance_before_kobo BIGINT
  balance_after_kobo  BIGINT
  description         TEXT
  counterpart_wallet_id UUID    nullable (populated on transfers)
  idempotency_key     TEXT      nullable
  created_at          TIMESTAMPTZ
  INDEX (wallet_id, created_at DESC)  ← for paginated statement

audit_logs
  id               UUID      PK
  wallet_id        UUID      FK → wallets
  event_type       TEXT
  balance_before   BIGINT
  balance_after    BIGINT
  amount           BIGINT    nullable
  actor            TEXT      nullable
  correlation_id   TEXT      nullable
  created_at       TIMESTAMPTZ
  INDEX (wallet_id, created_at DESC)

idempotency_records
  key              TEXT      PK
  request_hash     TEXT      NOT NULL
  wallet_id        UUID
  status           TEXT      'Processing' | 'Completed'
  response_status  INT       nullable
  response_body    JSONB     nullable
  created_at       TIMESTAMPTZ
  expires_at       TIMESTAMPTZ
  INDEX (expires_at)  ← for future TTL cleanup

outbox_messages
  id           UUID      PK
  type         TEXT
  payload      JSONB
  processed    BOOLEAN   DEFAULT false
  created_at   TIMESTAMPTZ
  processed_at TIMESTAMPTZ nullable
  INDEX (processed, created_at) WHERE NOT processed  ← partial index for poller
```

The `CHECK (balance_kobo >= 0)` constraint on `wallets` is defence-in-depth: even if a bug bypasses the domain-layer `Debit()` method, the database will reject the write.

---

## Testing Strategy

**Unit tests (38 tests, run in ~1s, no Docker needed)**

Tests are in `NovaWallet.Tests/Unit/`. Each test class isolates one service method using NSubstitute mocks for all dependencies. FluentAssertions provides readable assertions. Key coverage:

- `WalletService` — all happy paths and every domain exception path
- `Wallet` domain entity — `Credit`/`Debit` invariant enforcement
- Validators — valid and invalid inputs for all three request types

**Integration tests (require Docker — Testcontainers)**

Tests are in `NovaWallet.Tests/Integration/`. `IntegrationTestBase` spins up a real PostgreSQL 16 container via Testcontainers, runs migrations, and creates a `WebApplicationFactory<Program>` pointed at that database. Each test class resets the database between tests (`TRUNCATE ... CASCADE`).

**Concurrency tests (4 tests, deterministic)**

All concurrency tests use a `CountdownEvent` barrier — all threads are created first, then released simultaneously. No `Thread.Sleep` timers that would make tests flaky. Key tests:

| Test | Scenario | Assertion |
|---|---|---|
| `ConcurrentDebits_NeverAllowNegativeBalance` | 20 threads, only 5 should succeed | Final balance = 0, never goes negative |
| `ConcurrentBidirectionalTransfers_NoDeadlock_CorrectBalances` | A→B and B→A simultaneously | No deadlock, total money conserved |
| `ConcurrentIdempotentTransfers_SameKey_ExactlyOnceDebit` | 10 threads, same idempotency key | Exactly one debit |
| `ConcurrentTransfers_DailyLimitNeverExceeded` | 20 threads, total would exceed limit | Total outbound ≤ 50,000,000 kobo |

---

## Alternatives Considered

### Concurrency

| Option | Why not chosen |
|---|---|
| **Optimistic concurrency (EF Core `RowVersion`)** | Requires application-level retry loop. A bug in the retry logic (off-by-one, missing re-fetch) could allow a negative balance. The lock-hold time with FOR UPDATE is milliseconds — queuing cost is negligible. |
| **Application-level mutex / Redis lock** | Adds a distributed dependency. Failure mode: Redis goes down, transfers stop entirely or the lock expires mid-transfer. PostgreSQL row locks are durable and scoped to the transaction. |
| **SERIALIZABLE isolation** | Stronger than needed. Prevents phantom reads across the whole transaction but adds significant overhead and increases the abort rate under load. READ COMMITTED + explicit row locks achieves the same correctness for this workload. |

### Idempotency

| Option | Why not chosen |
|---|---|
| **Check-then-insert (two SQL round-trips)** | TOCTOU race: two concurrent requests can both read "no record" and both attempt the transfer. `ON CONFLICT DO NOTHING` is atomic at the database level. |
| **Update idempotency record after `CommitTransaction`** | The window between commit and update allows a retry to double-debit if the process crashes. Updating inside the transaction eliminates this window. |
| **Idempotency in a separate Redis cache** | Same availability concern as Redis locks above, plus cache eviction could lose the idempotency record and allow a replay. |

### Monetary representation

| Option | Why not chosen |
|---|---|
| **`decimal` in C# / `NUMERIC` in PostgreSQL** | Slightly more human-readable but `decimal` arithmetic is slower and the kobo integer representation is lossless with no rounding at any layer. Nigerian fintech regulations denominate in kobo. |
| **`double` / `float`** | Never acceptable for money. `0.1 + 0.2 ≠ 0.3` in IEEE 754. Rejected outright. |

### Timezone handling

| Option | Why not chosen |
|---|---|
| **`TimeZoneInfo.FindSystemTimeZoneById("Africa/Lagos")` / `"W. Central Africa Standard Time"`** | Requires OS timezone data. Different ID on Windows vs Linux. Fails silently in minimal containers. Nigeria has not observed DST since 1960 and has no scheduled changes — the full IANA machinery adds a failure mode with no benefit. |
| **Store all timestamps in WAT** | Mixing UTC and local time in the same database is a common source of bugs. All timestamps are UTC; the WAT boundary is computed only at the point of the daily limit query. |

### Outbox

| Option | Why not chosen |
|---|---|
| **Publish event directly in `TransferAsync`** | If the message broker is down, the transfer fails. If the broker call succeeds but the DB commit fails, an event is published for a transfer that never happened. The outbox decouples durability from delivery. |
| **Transactional outbox via CDC (Debezium)** | More robust for high-throughput scenarios but requires Kafka + Debezium infrastructure. The polling-based outbox is sufficient for this scale and has zero additional infrastructure. |
