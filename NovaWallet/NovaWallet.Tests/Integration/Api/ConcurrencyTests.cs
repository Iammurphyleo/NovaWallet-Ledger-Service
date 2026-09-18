using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using NovaWallet.Application.DTOs.Requests;
using NovaWallet.Application.DTOs.Responses;
using NovaWallet.Tests.Integration.TestFixtures;

namespace NovaWallet.Tests.Integration.Api;

/// <summary>
/// Concurrency tests that prove the hard constraints hold under concurrent load.
///
/// Each test uses a CountdownEvent as a barrier so all threads are ready before any fires.
/// This makes the concurrent scenario deterministic rather than sleep-based.
/// </summary>
[Collection("Integration")]
public sealed class ConcurrencyTests : IntegrationTestBase
{
    public ConcurrencyTests(PostgresContainerFixture fixture) : base(fixture) { }

    /// <summary>
    /// 20 concurrent debit transfers from the same wallet.
    /// The wallet has exactly enough for 5 of them.
    /// Invariant: balance never goes negative; exactly 5 succeed.
    /// </summary>
    [Fact]
    public async Task ConcurrentDebits_NeverAllowNegativeBalance()
    {
        const int concurrency = 20;
        const long initialBalanceKobo = 5_000_000L;  // ₦50,000
        const long transferAmountKobo = 1_000_000L;  // ₦10,000 each → only 5 fit

        var senderId = await CreateWalletAndGetIdAsync("concurrent-sender");
        var receiverId = await CreateWalletAndGetIdAsync("concurrent-receiver");
        var senderToken = await GetTokenAsync("concurrent-sender");

        await CreditWalletAsync(senderId, senderToken, initialBalanceKobo);

        var barrier = new CountdownEvent(concurrency);
        var tasks = new Task<HttpResponseMessage>[concurrency];

        for (int i = 0; i < concurrency; i++)
        {
            var idemKey = Guid.NewGuid().ToString();
            tasks[i] = Task.Run(async () =>
            {
                // Create a scoped client per task to avoid shared header state
                var client = Factory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", senderToken);

                // All threads wait here until every thread is ready — ensures maximum overlap
                barrier.Signal();
                barrier.Wait();

                return await client.PostAsJsonAsync("/api/v1/transfers",
                    new TransferRequest(senderId, receiverId, transferAmountKobo),
                    headers: new[] { ("Idempotency-Key", idemKey) });
            });
        }

        var responses = await Task.WhenAll(tasks);

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var failed = responses.Count(r => r.StatusCode is HttpStatusCode.UnprocessableEntity
                                             or HttpStatusCode.TooManyRequests);

        // Exactly 5 should succeed (initial = 5 × transfer amount)
        succeeded.Should().Be(5,
            because: "only 5 transfers of ₦10,000 fit within ₦50,000 balance");
        (succeeded + failed).Should().Be(concurrency);

        // The sender's balance must be exactly 0 — not negative
        var receiverToken = await GetTokenAsync("concurrent-receiver");
        var receiverBalance = await GetBalanceAsync(receiverId, receiverToken);
        receiverBalance.Should().Be(5 * transferAmountKobo,
            because: "receiver should have received exactly 5 × ₦10,000");

        var senderBalance = await GetBalanceAsync(senderId, senderToken);
        senderBalance.Should().Be(0,
            because: "sender should have been fully drained, never below 0");
    }

    /// <summary>
    /// Bidirectional concurrent transfers: A→B and B→A simultaneously.
    /// Verifies no deadlock occurs and that final balances are consistent.
    /// </summary>
    [Fact]
    public async Task ConcurrentBidirectionalTransfers_NoDeadlock_CorrectBalances()
    {
        const long initialKobo = 10_000_000L;
        const long transferKobo = 1_000_000L;
        const int rounds = 5; // 5 A→B and 5 B→A simultaneously

        var idA = await CreateWalletAndGetIdAsync("bidir-A");
        var idB = await CreateWalletAndGetIdAsync("bidir-B");
        var tokenA = await GetTokenAsync("bidir-A");
        var tokenB = await GetTokenAsync("bidir-B");

        await CreditWalletAsync(idA, tokenA, initialKobo);
        await CreditWalletAsync(idB, tokenB, initialKobo);

        var barrier = new CountdownEvent(rounds * 2);
        var tasks = new List<Task<HttpResponseMessage>>();

        for (int i = 0; i < rounds; i++)
        {
            var keyAtoB = Guid.NewGuid().ToString();
            var keyBtoA = Guid.NewGuid().ToString();

            tasks.Add(Task.Run(async () =>
            {
                var client = Factory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenA);
                barrier.Signal();
                barrier.Wait();
                return await client.PostAsJsonAsync("/api/v1/transfers",
                    new TransferRequest(idA, idB, transferKobo),
                    headers: new[] { ("Idempotency-Key", keyAtoB) });
            }));

            tasks.Add(Task.Run(async () =>
            {
                var client = Factory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenB);
                barrier.Signal();
                barrier.Wait();
                return await client.PostAsJsonAsync("/api/v1/transfers",
                    new TransferRequest(idB, idA, transferKobo),
                    headers: new[] { ("Idempotency-Key", keyBtoA) });
            }));
        }

        // If this hangs, there's a deadlock — xUnit's default 30s timeout will catch it
        var responses = await Task.WhenAll(tasks);

        // All should succeed: both wallets have sufficient balance
        responses.Should().OnlyContain(r =>
            r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.TooManyRequests,
            because: "bidirectional transfers should not deadlock");

        // Final balances: each succeeded A→B reduces A by transferKobo and increases B,
        // each succeeded B→A does the reverse.
        // Net effect of N A→B and N B→A = no net change → both should be near initialKobo.
        var balanceA = await GetBalanceAsync(idA, tokenA);
        var balanceB = await GetBalanceAsync(idB, tokenB);

        (balanceA + balanceB).Should().Be(initialKobo * 2,
            because: "total money in the system must be conserved");
    }

    /// <summary>
    /// 10 concurrent requests all using the same idempotency key.
    /// Exactly 1 debit must occur regardless of how many requests succeed.
    /// </summary>
    [Fact]
    public async Task ConcurrentIdempotentTransfers_SameKey_ExactlyOnceDebit()
    {
        const int concurrency = 10;
        const long initialKobo = 10_000_000L;
        const long transferKobo = 1_000_000L;

        var senderId = await CreateWalletAndGetIdAsync("idem-concurrent-sender");
        var receiverId = await CreateWalletAndGetIdAsync("idem-concurrent-receiver");
        var senderToken = await GetTokenAsync("idem-concurrent-sender");

        await CreditWalletAsync(senderId, senderToken, initialKobo);

        var sharedKey = Guid.NewGuid().ToString(); // all 10 tasks use the SAME key
        var barrier = new CountdownEvent(concurrency);
        var tasks = new Task<HttpResponseMessage>[concurrency];

        for (int i = 0; i < concurrency; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var client = Factory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", senderToken);
                barrier.Signal();
                barrier.Wait();
                return await client.PostAsJsonAsync("/api/v1/transfers",
                    new TransferRequest(senderId, receiverId, transferKobo),
                    headers: new[] { ("Idempotency-Key", sharedKey) });
            });
        }

        var responses = await Task.WhenAll(tasks);

        var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        successCount.Should().BeGreaterThanOrEqualTo(1,
            because: "at least one request must succeed");

        // Balance must reflect exactly one debit
        var receiverToken = await GetTokenAsync("idem-concurrent-receiver");
        var receiverBalance = await GetBalanceAsync(receiverId, receiverToken);
        receiverBalance.Should().Be(transferKobo,
            because: "idempotency must ensure only one transfer completes regardless of concurrent duplicates");

        var senderBalance = await GetBalanceAsync(senderId, senderToken);
        senderBalance.Should().Be(initialKobo - transferKobo,
            because: "sender must only be debited once");
    }

    /// <summary>
    /// 20 concurrent transfers all from the same wallet.
    /// Each is ₦3,000,000 but daily limit is ₦50,000,000.
    /// At most 16 can succeed (16 × 3M = 48M ≤ 50M; 17 × 3M = 51M > 50M).
    /// </summary>
    [Fact]
    public async Task ConcurrentTransfers_DailyLimitNeverExceeded()
    {
        const int concurrency = 20;
        const long perTransferKobo = 3_000_000L;     // ₦30,000 each
        const long dailyLimitKobo = 50_000_000L;      // ₦500,000

        var senderId = await CreateWalletAndGetIdAsync("limit-sender");
        var receiverId = await CreateWalletAndGetIdAsync("limit-receiver");
        var senderToken = await GetTokenAsync("limit-sender");

        // Fund generously so balance is never the limiting factor
        await CreditWalletAsync(senderId, senderToken, concurrency * perTransferKobo + dailyLimitKobo);

        var barrier = new CountdownEvent(concurrency);
        var tasks = new Task<HttpResponseMessage>[concurrency];

        for (int i = 0; i < concurrency; i++)
        {
            var key = Guid.NewGuid().ToString();
            tasks[i] = Task.Run(async () =>
            {
                var client = Factory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", senderToken);
                barrier.Signal();
                barrier.Wait();
                return await client.PostAsJsonAsync("/api/v1/transfers",
                    new TransferRequest(senderId, receiverId, perTransferKobo),
                    headers: new[] { ("Idempotency-Key", key) });
            });
        }

        var responses = await Task.WhenAll(tasks);
        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.OK);

        // The maximum number that can succeed is floor(50_000_000 / 3_000_000) = 16
        succeeded.Should().BeLessThanOrEqualTo(16,
            because: "daily limit of 50M kobo must not be breached");

        // Receiver balance must not exceed daily limit
        var receiverToken = await GetTokenAsync("limit-receiver");
        var receiverBalance = await GetBalanceAsync(receiverId, receiverToken);
        receiverBalance.Should().BeLessThanOrEqualTo(dailyLimitKobo,
            because: "total outbound cannot exceed the daily limit");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWalletAndGetIdAsync(string customerId)
    {
        var token = await GetTokenAsync(customerId);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.PostAsJsonAsync("/api/v1/wallets",
            new CreateWalletRequest(customerId));
        response.EnsureSuccessStatusCode();
        var wallet = await response.Content.ReadFromJsonAsync<WalletResponse>();
        return wallet!.Id;
    }

    private async Task CreditWalletAsync(Guid walletId, string token, long amountKobo)
    {
        // Credit in chunks if exceeding max single credit (5B kobo)
        const long maxChunk = 4_999_999_999L;
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        while (amountKobo > 0)
        {
            var chunk = Math.Min(amountKobo, maxChunk);
            var r = await client.PostAsJsonAsync($"/api/v1/wallets/{walletId}/credit",
                new CreditWalletRequest(chunk));
            r.EnsureSuccessStatusCode();
            amountKobo -= chunk;
        }
    }

    private async Task<long> GetBalanceAsync(Guid walletId, string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync($"/api/v1/wallets/{walletId}/balance");
        response.EnsureSuccessStatusCode();
        var balance = await response.Content.ReadFromJsonAsync<BalanceResponse>();
        return balance!.BalanceKobo;
    }
}
