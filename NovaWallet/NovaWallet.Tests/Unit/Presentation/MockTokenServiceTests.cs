using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NovaWallet.Presentation.Services;
using NovaWallet.Presentation.Settings;

namespace NovaWallet.Tests.Unit.Presentation;

public sealed class MockTokenServiceTests
{
    private static readonly JwtSettings Settings = new()
    {
        SigningKey = "unit-test-signing-key-at-least-32-chars!",
        Issuer = "test-issuer",
        Audience = "test-audience",
        ExpiryMinutes = 60
    };

    private readonly MockTokenService _sut = new(Options.Create(Settings));

    [Fact]
    public void Issue_ValidCustomerId_ReturnsNonEmptyToken()
    {
        var result = _sut.Issue("cust-1");

        result.Token.Should().NotBeNullOrWhiteSpace();
        result.CustomerId.Should().Be("cust-1");
    }

    [Fact]
    public void Issue_ValidCustomerId_TokenContainsCorrectSubClaim()
    {
        var result = _sut.Issue("cust-abc");

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(result.Token);

        jwt.Subject.Should().Be("cust-abc");
        jwt.Issuer.Should().Be(Settings.Issuer);
        jwt.Audiences.Should().Contain(Settings.Audience);
    }

    [Fact]
    public void Issue_ValidCustomerId_ExpiryIsInFuture()
    {
        var result = _sut.Issue("cust-1");

        result.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
        result.ExpiresAt.Should().BeCloseTo(
            DateTime.UtcNow.AddMinutes(Settings.ExpiryMinutes),
            precision: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Issue_TwoCalls_ProduceDifferentJtiClaims()
    {
        var first = _sut.Issue("cust-1");
        var second = _sut.Issue("cust-1");

        var handler = new JwtSecurityTokenHandler();
        var jti1 = handler.ReadJwtToken(first.Token).Id;
        var jti2 = handler.ReadJwtToken(second.Token).Id;

        jti1.Should().NotBe(jti2);
    }
}
