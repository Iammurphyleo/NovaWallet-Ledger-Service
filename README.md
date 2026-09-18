# NovaWallet Ledger Service

A production-grade wallet ledger backend for the fictional FirstBank NovaPay super-app. Implements the `NovaWallet` module: create wallets, credit, P2P transfer (with idempotency and daily limits), transaction history, and an append-only audit trail.

---

## Quick Start

```bash
docker compose up --build
```

The API will be available at **http://localhost:8080** with Swagger UI at the root.  
PostgreSQL EF Core migrations run automatically on startup.

---

## Architecture

```
NovaWallet.Domain          Entities, domain exceptions, repository interfaces.
                           No external dependencies — pure domain logic.

NovaWallet.Application     Service layer (WalletService), DTOs, FluentValidation validators.
                           Orchestrates all business rules.

NovaWallet.Infrastructure  EF Core + Npgsql (PostgreSQL), repository implementations,
                           UnitOfWork, OutboxProcessorService background task.

NovaWallet.Presentation    ASP.NET Core Web API — controllers, JWT middleware,
                           error handling middleware, Swagger, rate limiting, health checks.

NovaWallet.Tests           xUnit unit tests (mocked) + integration tests (Testcontainers).
```

**Project references follow a strict dependency rule:**  
`Presentation → Application → Domain`  
`Infrastructure → Domain` (no Application → Infrastructure direction)

---

## Key Design Decisions

### Money: integers in kobo, always
Every amount is stored as `BIGINT` in PostgreSQL, transported as `long` in C#. No `float`, `double`, or `decimal` anywhere in the money path. `long` can represent ₦92 trillion — safely above any realistic wallet balance.

### Concurrency: pessimistic row-level locking
The transfer endpoint issues `SELECT ... FOR UPDATE` on both wallet rows before mutating them. This prevents lost updates and double-spends without optimistic retry complexity.

**Deadlock prevention:** locks are always acquired in ascending wallet UUID order. If A→B and B→A transfers arrive simultaneously, both try to lock the lower UUID first — only one proceeds, the other waits. No deadlock is possible.

This is tested by `ConcurrentBidirectionalTransfers_NoDeadlock_CorrectBalances`.

### Idempotency: two-phase claim with `ON CONFLICT DO NOTHING`
1. Compute SHA-256 of the canonicalized request body.
2. `INSERT INTO idempotency_records ... ON CONFLICT DO NOTHING` — atomic check-and-claim.
3. If 0 rows inserted: read the existing record; return cached response (same payload) or 422 (different payload).
4. Execute the transfer in its own transaction.
5. Update the idempotency record to `COMPLETED` with the serialized response body.
6. On any failure: delete the `PROCESSING` record so the client can retry.

Concurrent requests with the same key are handled by the unique constraint — only one wins the `INSERT` race.

### Daily outbound limit (WAT reset)
WAT (West Africa Time) is **always UTC+1** — Nigeria does not observe DST, so no IANA timezone lookup is needed. Midnight WAT is computed as `UTC midnight + 1 hour back` which is `UTC 23:00 the previous day`. The daily debit total is queried inside the locked transfer transaction for consistency.

### Audit log: append-only by design
`AuditLog` has no FK to `wallets` (intentional — audit records must outlive wallet records). The repository exposes only `AddAsync`. In a production environment the DB role used by the service would have `INSERT`-only privileges on `audit_logs`.

### Outbox pattern (stretch goal)
Every successful transfer atomically writes an `OutboxMessage` in the same transaction. A `BackgroundService` (OutboxProcessorService) polls every 5 seconds and "publishes" the event — for this exercise by emitting a structured log line. In production this would publish to Kafka or RabbitMQ.

### Structured logging with correlation IDs (stretch goal)
`CorrelationIdMiddleware` reads (or generates) `X-Correlation-Id` on every request, echoes it in the response header, and calls `ILogger.BeginScope` so all log lines within the request carry `CorrelationId`.

### Rate limiting (stretch goal)
Fixed-window rate limiter on `POST /api/v1/transfers`: 10 requests per minute per client. Excess requests return HTTP 429.

---

## API Endpoints

All endpoints (except `/api/v1/auth/token` and health) require a JWT bearer token.

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/v1/auth/token` | Issue a mock JWT for testing |
| `POST` | `/api/v1/wallets` | Create a wallet |
| `GET`  | `/api/v1/wallets/{id}/balance` | Get balance |
| `POST` | `/api/v1/wallets/{id}/credit` | Credit a wallet |
| `POST` | `/api/v1/transfers` | Transfer funds (`Idempotency-Key` header required) |
| `GET`  | `/api/v1/wallets/{id}/statement` | Paginated transaction history |
| `GET`  | `/health/live` | Liveness probe |
| `GET`  | `/health/ready` | Readiness probe (checks DB) |

Swagger UI is available at **http://localhost:8080** (or `/swagger`).

### Getting a token

```bash
curl -X POST http://localhost:8080/api/v1/auth/token \
  -H 'Content-Type: application/json' \
  -d '{"customerId": "alice"}'
```

Use the returned `token` as `Authorization: Bearer <token>` on subsequent requests.

### Transfer example

```bash
curl -X POST http://localhost:8080/api/v1/transfers \
  -H 'Content-Type: application/json' \
  -H 'Authorization: Bearer <token>' \
  -H 'Idempotency-Key: 550e8400-e29b-41d4-a716-446655440000' \
  -d '{
    "sourceWalletId": "<sender-wallet-id>",
    "destinationWalletId": "<receiver-wallet-id>",
    "amountKobo": 1000000,
    "description": "Rent payment"
  }'
```

---

## Error Responses

All errors follow **RFC 7807 Problem Details**:

```json
{
  "type": "https://novawallet.io/errors/insufficient-funds",
  "title": "Insufficient Funds",
  "status": 422,
  "detail": "Insufficient funds. Available: 500000 kobo, Required: 1000000 kobo.",
  "instance": "/api/v1/transfers",
  "correlationId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

---

## Running Tests

### Unit tests (no Docker required)
```bash
dotnet test --filter "FullyQualifiedName~Unit"
```
38 tests — domain entity invariants, service business rules, validators, error paths.

### Integration tests (requires Docker)
```bash
dotnet test --filter "FullyQualifiedName~Integration"
```
Integration tests use **Testcontainers** to spin up a real PostgreSQL 16 container.  
They cover: all API endpoints, auth, ownership enforcement, idempotency replay, daily limit enforcement, and **four concurrent load scenarios**:

- `ConcurrentDebits_NeverAllowNegativeBalance` — 20 concurrent debits on the same wallet; exactly 5 succeed; balance never goes negative.
- `ConcurrentBidirectionalTransfers_NoDeadlock_CorrectBalances` — simultaneous A→B and B→A transfers; no deadlock; total money conserved.
- `ConcurrentIdempotentTransfers_SameKey_ExactlyOnceDebit` — 10 threads all send the same idempotency key; exactly one debit occurs.
- `ConcurrentTransfers_DailyLimitNeverExceeded` — 20 concurrent transfers; total outbound never exceeds ₦500,000/day limit.

> **Note for Windows + Docker Desktop WSL2 mode:** Testcontainers requires the Docker daemon to be accessible via named pipe or TCP. If you see a `DockerEndpointAuthConfig` error, ensure Docker Desktop has "Expose daemon on tcp://localhost:2375 without TLS" enabled, or that the `docker_engine` named pipe is accessible. Alternatively, run: `docker run -p 5432:5432 -e POSTGRES_DB=novawallet_test -e POSTGRES_USER=test -e POSTGRES_PASSWORD=test_secret postgres:16-alpine` and set `ConnectionStrings__DefaultConnection` accordingly.

### Run all tests
```bash
dotnet test
```

---

## Database

PostgreSQL 16. Schema applied via EF Core migrations on startup.

| Table | Purpose |
|-------|---------|
| `wallets` | Wallet balances with `CHECK (balance_kobo >= 0)` constraint |
| `wallet_transactions` | Immutable transaction ledger; unique `reference` per entry |
| `audit_logs` | Append-only audit trail; no FK to wallets |
| `idempotency_records` | Idempotency key store with JSONB response cache |
| `outbox_messages` | Transactional outbox for `TransferCompleted` events |

---

## Configuration

| Variable | Default | Description |
|---------|---------|-------------|
| `ConnectionStrings__DefaultConnection` | (see appsettings.json) | PostgreSQL connection string |
| `Jwt__SigningKey` | dev key | **Must be changed in production** |
| `Jwt__Issuer` | `novawallet-mock` | JWT issuer claim |
| `Jwt__Audience` | `novawallet-api` | JWT audience claim |
| `Jwt__ExpiryMinutes` | `60` | Token lifetime |
| `Wallet__DailyOutboundLimitKobo` | `50000000` | Daily outbound limit (₦500,000) |

---

## Assumptions Documented

1. **WAT offset hardcoded as UTC+1.** Nigeria has never observed DST and has no plans to — hardcoding is more reliable than IANA timezone resolution across OS platforms.
2. **Credit endpoint has no ownership restriction.** The spec models it as a simulated inbound NIP transfer (from a bank rail, not a peer). In a real system, credits would come from an authenticated NIP callback, not a customer-facing endpoint.
3. **Idempotency keys are client-generated.** The server does not mint them. Clients should use UUIDs. Keys expire after 24 hours (configurable).
4. **Single wallet per customer.** The spec says "create a wallet for a customer id" without mentioning multiple wallets. A `UNIQUE` constraint on `customer_id` enforces this.
5. **Mock JWT issuer.** The `AuthController` issues tokens for any `customerId` — authentication is real (JWT validation, claims, ownership checks), but the identity store is not. The spec explicitly asks for a simplified/mock issuer.

---

## Trade-offs

| Decision | Chosen | Alternative | Reason |
|----------|--------|------------|--------|
| Concurrency | Pessimistic locking | Optimistic + retry | No retry storms; invariant is too critical to risk |
| Test database | Testcontainers PostgreSQL | EF Core InMemory | `FOR UPDATE`, `CHECK` constraints, `ON CONFLICT DO NOTHING` require real PostgreSQL |
| Service pattern | Direct service calls | MediatR CQRS | Project is small; MediatR would be over-engineering |
| Options binding | `Configure<T>` in Presentation | BindConfiguration in Application | Keeps Application layer configuration-agnostic |
