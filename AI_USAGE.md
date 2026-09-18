# AI Usage — NovaWallet Ledger Service

This document records how Claude was used during the development of this service, concrete prompts given, and — most importantly — one case where the AI's output was wrong for a financial system and how it was caught and corrected.

---

## Tools Used

**Claude Sonnet 4.6 (via Claude Code CLI)**

Used throughout the implementation for:
- Architecture planning (layer structure, concurrency strategy, idempotency design)
- Code generation (entity models, service logic, EF Core configurations, tests)
- Iterative debugging (compile errors, NuGet version conflicts, Docker connectivity issues)

---

## Concrete Prompts and Outcomes

### Prompt 1: Architecture planning

> "I have a .NET clean architecture skeleton (Domain, Application, Infrastructure, Presentation, Tests — all empty). I need to implement a wallet ledger with: create wallet, get balance, credit, P2P transfer with idempotency and a daily WAT limit, paginated statement, and an append-only audit log. What is the right concurrency strategy for transfers?"

**What came back:**  
Claude described two approaches: optimistic concurrency (row version, retry) and pessimistic locking (SELECT FOR UPDATE). It recommended pessimistic locking for a financial system because the non-negative balance invariant is too critical to risk even one failed retry getting through, and the lock hold time is very short (milliseconds). It also flagged the deadlock risk when two bidirectional transfers happen simultaneously and proposed the ordered-lock-acquisition pattern (always lock the lower UUID first).

**Outcome:** Used this strategy. The deadlock prevention is tested in `ConcurrentBidirectionalTransfers_NoDeadlock_CorrectBalances`.

---

### Prompt 2: Idempotency key design

> "Design the idempotency key behavior for the transfer endpoint. The key must: not double-process a replay with the same payload, reject a replay with a different payload, handle concurrent requests with the same key (race condition), and survive a service crash mid-processing."

**What came back:**  
Claude proposed a two-phase approach: (1) `INSERT ... ON CONFLICT DO NOTHING` to atomically claim the key before executing the transfer, (2) execute the transfer in its own transaction, (3) update the record to COMPLETED. For the crash case (key stuck in PROCESSING), it suggested a configurable stale timeout after which a PROCESSING key is re-claimable.

**Outcome:** Implemented as designed. The `IIdempotencyRepository.TryInsertAsync` uses raw SQL with `ON CONFLICT DO NOTHING` for the atomic claim.

---

### Prompt 3: Daily limit WAT timezone reset

> "The daily outbound limit resets at midnight WAT (West Africa Time). How should I compute the WAT midnight boundary in C# for a PostgreSQL query, in a way that works on both Windows and Linux?"

**What came back (initial — WRONG for a financial system):**  
Claude initially suggested using `TimeZoneInfo.FindSystemTimeZoneById("Africa/Lagos")` and noted that on Windows the ID would be `"W. Central Africa Standard Time"` while on Linux it's `"Africa/Lagos"`. It provided a runtime OS check using `RuntimeInformation.IsOSPlatform`.

**Why this was wrong / what was caught and fixed:**  
The IANA timezone database approach is fragile: it depends on OS-level timezone data being present and up to date, and using `RuntimeInformation.IsOSPlatform` adds a runtime conditional that can silently break in containerized environments where the Windows timezone registry isn't present. More importantly, **WAT has never observed DST and has no scheduled changes** — it is a fixed UTC+1 offset. Using the full IANA resolution machinery for a fixed offset is over-engineering that introduces a failure mode (missing timezone data) with no benefit.

**Fix applied:**  
Replaced with a hardcoded `TimeSpan.FromHours(1)` offset:

```csharp
// WAT is always UTC+1, no DST — hardcoding is more reliable than IANA on any OS
private static readonly TimeSpan WatOffset = TimeSpan.FromHours(1);

private static DateTime GetTodayWatMidnightUtc()
{
    var watNow = DateTime.UtcNow.Add(WatOffset);
    var watMidnight = watNow.Date;
    return watMidnight.Subtract(WatOffset);
}
```

This is simpler, has no runtime dependencies, works identically on Windows, Linux, and inside Docker containers with no timezone data, and correctly handles the one case that matters: converting "midnight WAT" to UTC. The assumption is documented in `README.md`.

---

## Summary

AI was used to move faster through boilerplate (EF Core configurations, test scaffolding) and to reason about correctness of concurrent designs. The key judgment exercised was catching the IANA timezone approach: an AI that doesn't know the regulatory/operational context of Nigerian fintech naturally reaches for the "complete" solution (full timezone library), but for this domain the simple fixed-offset is both more correct and more robust. AI tools accelerate; engineering judgment directs.
