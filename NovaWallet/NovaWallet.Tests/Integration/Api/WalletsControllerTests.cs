using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using NovaWallet.Application.DTOs.Requests;
using NovaWallet.Application.DTOs.Responses;
using NovaWallet.Tests.Integration.TestFixtures;

namespace NovaWallet.Tests.Integration.Api;

[Collection("Integration")]
public sealed class WalletsControllerTests : IntegrationTestBase
{
    public WalletsControllerTests(PostgresContainerFixture fixture) : base(fixture) { }

    // ── Auth ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AllEndpoints_WithoutToken_Return401()
    {
        var response = await Client.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}/balance");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Create wallet ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateWallet_ValidRequest_Returns201WithWallet()
    {
        var token = await GetTokenAsync("customer-A");
        AuthorizeAs(token);

        var response = await Client.PostAsJsonAsync("/api/v1/wallets",
            new CreateWalletRequest("customer-A"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var wallet = await response.Content.ReadFromJsonAsync<WalletResponse>();
        wallet!.CustomerId.Should().Be("customer-A");
        wallet.BalanceKobo.Should().Be(0);
        wallet.Currency.Should().Be("NGN");
    }

    [Fact]
    public async Task CreateWallet_DuplicateCustomer_Returns409()
    {
        var token = await GetTokenAsync("dup-customer");
        AuthorizeAs(token);

        await Client.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest("dup-customer"));
        var response = await Client.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest("dup-customer"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateWallet_InvalidCustomerId_Returns400()
    {
        var token = await GetTokenAsync("any");
        AuthorizeAs(token);

        var response = await Client.PostAsJsonAsync("/api/v1/wallets",
            new CreateWalletRequest("invalid id with spaces"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Get balance ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBalance_ExistingWallet_ReturnsBalance()
    {
        var walletId = await CreateWalletAndGetIdAsync("bal-customer");
        var token = await GetTokenAsync("bal-customer");
        AuthorizeAs(token);

        var response = await Client.GetAsync($"/api/v1/wallets/{walletId}/balance");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var balance = await response.Content.ReadFromJsonAsync<BalanceResponse>();
        balance!.BalanceKobo.Should().Be(0);
        balance.Currency.Should().Be("NGN");
    }

    [Fact]
    public async Task GetBalance_NonExistentWallet_Returns404()
    {
        var token = await GetTokenAsync("ghost-customer");
        AuthorizeAs(token);

        var response = await Client.GetAsync($"/api/v1/wallets/{Guid.NewGuid()}/balance");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetBalance_WrongOwner_Returns403()
    {
        var walletId = await CreateWalletAndGetIdAsync("owner");
        var attackerToken = await GetTokenAsync("attacker");
        AuthorizeAs(attackerToken);

        var response = await Client.GetAsync($"/api/v1/wallets/{walletId}/balance");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Credit ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreditWallet_ValidAmount_Returns200AndUpdatesBalance()
    {
        var walletId = await CreateWalletAndGetIdAsync("credit-customer");
        var token = await GetTokenAsync("credit-customer");
        AuthorizeAs(token);

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/wallets/{walletId}/credit",
            new CreditWalletRequest(1_000_000, "Test credit"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var balanceResponse = await Client.GetAsync($"/api/v1/wallets/{walletId}/balance");
        var balance = await balanceResponse.Content.ReadFromJsonAsync<BalanceResponse>();
        balance!.BalanceKobo.Should().Be(1_000_000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task CreditWallet_InvalidAmount_Returns400(long amount)
    {
        var walletId = await CreateWalletAndGetIdAsync("cr-bad");
        var token = await GetTokenAsync("cr-bad");
        AuthorizeAs(token);

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/wallets/{walletId}/credit", new CreditWalletRequest(amount));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Transfer ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transfer_ValidTransfer_DeductsSenderCreditsReceiver()
    {
        var senderId = await CreateWalletAndGetIdAsync("sender");
        var receiverId = await CreateWalletAndGetIdAsync("receiver");

        var senderToken = await GetTokenAsync("sender");
        AuthorizeAs(senderToken);
        await Client.PostAsJsonAsync($"/api/v1/wallets/{senderId}/credit",
            new CreditWalletRequest(10_000_000));

        var transferResponse = await Client.PostAsJsonAsync(
            "/api/v1/transfers",
            new TransferRequest(senderId, receiverId, 3_000_000),
            headers: new[] { ("Idempotency-Key", Guid.NewGuid().ToString()) });

        transferResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var senderBalance = await GetBalanceAsync(senderId, senderToken);
        senderBalance.Should().Be(7_000_000);

        // Check receiver balance with receiver's token
        var receiverToken = await GetTokenAsync("receiver");
        var receiverBalance = await GetBalanceAsync(receiverId, receiverToken);
        receiverBalance.Should().Be(3_000_000);
    }

    [Fact]
    public async Task Transfer_InsufficientFunds_Returns422()
    {
        var senderId = await CreateWalletAndGetIdAsync("broke-sender");
        var receiverId = await CreateWalletAndGetIdAsync("broke-receiver");

        var token = await GetTokenAsync("broke-sender");
        AuthorizeAs(token);

        var response = await Client.PostAsJsonAsync(
            "/api/v1/transfers",
            new TransferRequest(senderId, receiverId, 1),
            headers: new[] { ("Idempotency-Key", Guid.NewGuid().ToString()) });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Transfer_MissingIdempotencyKey_Returns400()
    {
        var senderId = await CreateWalletAndGetIdAsync("idem-sender");
        var receiverId = await CreateWalletAndGetIdAsync("idem-receiver");
        var token = await GetTokenAsync("idem-sender");
        AuthorizeAs(token);

        // No Idempotency-Key header
        using var content = System.Net.Http.Json.JsonContent.Create(
            new TransferRequest(senderId, receiverId, 1_000));
        var response = await Client.PostAsync("/api/v1/transfers", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Transfer_DailyLimitExceeded_Returns422()
    {
        var senderId = await CreateWalletAndGetIdAsync("daily-sender");
        var receiverId = await CreateWalletAndGetIdAsync("daily-receiver");
        var token = await GetTokenAsync("daily-sender");
        AuthorizeAs(token);

        // Credit more than the limit
        await Client.PostAsJsonAsync($"/api/v1/wallets/{senderId}/credit",
            new CreditWalletRequest(100_000_000L));

        // Transfer up to the limit
        await Client.PostAsJsonAsync("/api/v1/transfers",
            new TransferRequest(senderId, receiverId, 50_000_000L),
            headers: new[] { ("Idempotency-Key", Guid.NewGuid().ToString()) });

        // This one should fail: 1 kobo over the limit
        var response = await Client.PostAsJsonAsync("/api/v1/transfers",
            new TransferRequest(senderId, receiverId, 1L),
            headers: new[] { ("Idempotency-Key", Guid.NewGuid().ToString()) });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Transfer_IdempotentReplay_Returns200WithSameResult()
    {
        var senderId = await CreateWalletAndGetIdAsync("idem-replay-sender");
        var receiverId = await CreateWalletAndGetIdAsync("idem-replay-receiver");
        var token = await GetTokenAsync("idem-replay-sender");
        AuthorizeAs(token);

        await Client.PostAsJsonAsync($"/api/v1/wallets/{senderId}/credit",
            new CreditWalletRequest(10_000_000));

        var idemKey = Guid.NewGuid().ToString();
        var request = new TransferRequest(senderId, receiverId, 1_000_000);

        var first = await Client.PostAsJsonAsync("/api/v1/transfers", request,
            headers: new[] { ("Idempotency-Key", idemKey) });
        var second = await Client.PostAsJsonAsync("/api/v1/transfers", request,
            headers: new[] { ("Idempotency-Key", idemKey) });

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        // Balance should only have been debited once
        var balance = await GetBalanceAsync(senderId, token);
        balance.Should().Be(9_000_000);
    }

    [Fact]
    public async Task Transfer_IdempotentKeyDifferentPayload_Returns422()
    {
        var senderId = await CreateWalletAndGetIdAsync("conflict-sender");
        var receiverId = await CreateWalletAndGetIdAsync("conflict-receiver");
        var token = await GetTokenAsync("conflict-sender");
        AuthorizeAs(token);

        await Client.PostAsJsonAsync($"/api/v1/wallets/{senderId}/credit",
            new CreditWalletRequest(10_000_000));

        var idemKey = Guid.NewGuid().ToString();

        // First transfer
        await Client.PostAsJsonAsync("/api/v1/transfers",
            new TransferRequest(senderId, receiverId, 1_000_000),
            headers: new[] { ("Idempotency-Key", idemKey) });

        // Same key, different amount
        var response = await Client.PostAsJsonAsync("/api/v1/transfers",
            new TransferRequest(senderId, receiverId, 2_000_000),
            headers: new[] { ("Idempotency-Key", idemKey) });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ── Statement ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatement_AfterCreditsAndTransfer_ReturnsNewestFirst()
    {
        var senderId = await CreateWalletAndGetIdAsync("stmt-sender");
        var receiverId = await CreateWalletAndGetIdAsync("stmt-receiver");
        var senderToken = await GetTokenAsync("stmt-sender");
        AuthorizeAs(senderToken);

        await Client.PostAsJsonAsync($"/api/v1/wallets/{senderId}/credit",
            new CreditWalletRequest(10_000_000));
        await Client.PostAsJsonAsync("/api/v1/transfers",
            new TransferRequest(senderId, receiverId, 1_000_000),
            headers: new[] { ("Idempotency-Key", Guid.NewGuid().ToString()) });

        var response = await Client.GetAsync($"/api/v1/wallets/{senderId}/statement?page=1&pageSize=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var statement = await response.Content.ReadFromJsonAsync<PagedResponse<TransactionResponse>>();
        statement!.TotalCount.Should().Be(2);
        statement.Items[0].CreatedAt.Should().BeOnOrAfter(statement.Items[1].CreatedAt);
    }

    [Fact]
    public async Task GetStatement_InvalidPageSize_Returns400()
    {
        var walletId = await CreateWalletAndGetIdAsync("page-customer");
        var token = await GetTokenAsync("page-customer");
        AuthorizeAs(token);

        var response = await Client.GetAsync($"/api/v1/wallets/{walletId}/statement?page=1&pageSize=200");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetStatement_ZeroPage_Returns400()
    {
        var walletId = await CreateWalletAndGetIdAsync("page0-customer");
        var token = await GetTokenAsync("page0-customer");
        AuthorizeAs(token);

        var response = await Client.GetAsync($"/api/v1/wallets/{walletId}/statement?page=0&pageSize=10");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Health ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthLive_Returns200()
    {
        var response = await Client.GetAsync("/health/live");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthReady_Returns200()
    {
        var response = await Client.GetAsync("/health/ready");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWalletAndGetIdAsync(string customerId)
    {
        var token = await GetTokenAsync(customerId);
        var prevToken = Client.DefaultRequestHeaders.Authorization;
        AuthorizeAs(token);
        var response = await Client.PostAsJsonAsync("/api/v1/wallets", new CreateWalletRequest(customerId));
        var wallet = await response.Content.ReadFromJsonAsync<WalletResponse>();
        if (prevToken is not null)
            Client.DefaultRequestHeaders.Authorization = prevToken;
        return wallet!.Id;
    }

    private async Task<long> GetBalanceAsync(Guid walletId, string token)
    {
        var prevToken = Client.DefaultRequestHeaders.Authorization;
        Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await Client.GetAsync($"/api/v1/wallets/{walletId}/balance");
        response.EnsureSuccessStatusCode();
        var balance = await response.Content.ReadFromJsonAsync<BalanceResponse>();
        Client.DefaultRequestHeaders.Authorization = prevToken;
        return balance!.BalanceKobo;
    }
}

// Extension to PostAsJsonAsync with custom headers
internal static class HttpClientExtensions
{
    public static async Task<HttpResponseMessage> PostAsJsonAsync<T>(
        this HttpClient client, string url, T value,
        IEnumerable<(string Name, string Value)>? headers = null,
        CancellationToken ct = default)
    {
        using var content = System.Net.Http.Json.JsonContent.Create(value);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (headers is not null)
            foreach (var (name, val) in headers)
                request.Headers.Add(name, val);
        return await client.SendAsync(request, ct);
    }
}
