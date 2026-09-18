using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using NovaWallet.Tests.Integration.TestFixtures;

namespace NovaWallet.Tests.Integration.Api;

[Collection("Integration")]
public sealed class AuthControllerTests : IntegrationTestBase
{
    public AuthControllerTests(PostgresContainerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task IssueToken_ValidCustomerId_Returns200WithToken()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/token",
            new { customerId = "test-customer" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TokenResponseDto>();
        body!.Token.Should().NotBeNullOrWhiteSpace();
        body.CustomerId.Should().Be("test-customer");
        body.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task IssueToken_MissingCustomerIdField_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/token", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task IssueToken_EmptyCustomerId_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/token",
            new { customerId = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task IssueToken_IssuedToken_AuthenticatesSuccessfully()
    {
        // Token issued by the mock endpoint must be accepted by the wallet endpoints
        var tokenResponse = await Client.PostAsJsonAsync("/api/v1/auth/token",
            new { customerId = "auth-test-customer" });
        var body = await tokenResponse.Content.ReadFromJsonAsync<TokenResponseDto>();

        AuthorizeAs(body!.Token);

        // Create a wallet — if auth failed we'd get 401
        var walletResponse = await Client.PostAsJsonAsync("/api/v1/wallets",
            new { customerId = "auth-test-customer" });

        walletResponse.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private sealed record TokenResponseDto(string Token, DateTime ExpiresAt, string CustomerId);
}
