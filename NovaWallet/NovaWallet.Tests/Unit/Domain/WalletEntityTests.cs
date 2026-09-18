using FluentAssertions;
using NovaWallet.Domain.Entities;
using NovaWallet.Domain.Exceptions;

namespace NovaWallet.Tests.Unit.Domain;

public sealed class WalletEntityTests
{
    [Fact]
    public void Create_ValidCustomerId_ReturnsWalletWithZeroBalance()
    {
        var wallet = Wallet.Create("customer-001");

        wallet.Id.Should().NotBeEmpty();
        wallet.CustomerId.Should().Be("customer-001");
        wallet.BalanceKobo.Should().Be(0);
        wallet.Currency.Should().Be("NGN");
        wallet.IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_NullOrWhitespaceCustomerId_Throws(string? customerId)
    {
        var act = () => Wallet.Create(customerId!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Credit_PositiveAmount_IncreasesBalance()
    {
        var wallet = Wallet.Create("cust-01");
        wallet.Credit(5000);
        wallet.BalanceKobo.Should().Be(5000);
    }

    [Fact]
    public void Credit_MultipleCredits_Accumulates()
    {
        var wallet = Wallet.Create("cust-01");
        wallet.Credit(1_000_000);
        wallet.Credit(2_000_000);
        wallet.BalanceKobo.Should().Be(3_000_000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Credit_ZeroOrNegativeAmount_Throws(long amount)
    {
        var wallet = Wallet.Create("cust-01");
        var act = () => wallet.Credit(amount);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Debit_SufficientBalance_DecreasesBalance()
    {
        var wallet = Wallet.Create("cust-01");
        wallet.Credit(10_000);
        wallet.Debit(3_000);
        wallet.BalanceKobo.Should().Be(7_000);
    }

    [Fact]
    public void Debit_ExactBalance_ZeroesBalance()
    {
        var wallet = Wallet.Create("cust-01");
        wallet.Credit(5_000);
        wallet.Debit(5_000);
        wallet.BalanceKobo.Should().Be(0);
    }

    [Fact]
    public void Debit_InsufficientBalance_ThrowsInsufficientFundsException()
    {
        var wallet = Wallet.Create("cust-01");
        wallet.Credit(1_000);
        var act = () => wallet.Debit(1_001);
        act.Should().Throw<InsufficientFundsException>();
    }

    [Fact]
    public void Debit_ZeroBalance_ThrowsInsufficientFunds()
    {
        var wallet = Wallet.Create("cust-01");
        var act = () => wallet.Debit(1);
        act.Should().Throw<InsufficientFundsException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Debit_ZeroOrNegativeAmount_Throws(long amount)
    {
        var wallet = Wallet.Create("cust-01");
        wallet.Credit(10_000);
        var act = () => wallet.Debit(amount);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Debit_ReturnsBalanceBeforeDebit()
    {
        var wallet = Wallet.Create("cust-01");
        wallet.Credit(10_000);
        var before = wallet.Debit(3_000);
        before.Should().Be(10_000);
    }

    [Fact]
    public void Balance_NeverGoesNegative_UnderSequentialDebits()
    {
        var wallet = Wallet.Create("cust-01");
        wallet.Credit(100);

        wallet.Debit(50);
        wallet.BalanceKobo.Should().Be(50);

        var act = () => wallet.Debit(51);
        act.Should().Throw<InsufficientFundsException>();

        // Balance must still be 50 — the failed debit must not mutate state
        wallet.BalanceKobo.Should().Be(50);
    }

    [Fact]
    public void Credit_LargeAmount_NoOverflow()
    {
        // Max representable NGN balance at kobo precision should not overflow long
        // Long.MaxValue ≈ 9.2 × 10^18 kobo = ₦92 trillion — safely above any realistic balance
        var wallet = Wallet.Create("cust-01");
        wallet.Credit(50_000_000_000L); // ₦500,000,000
        wallet.BalanceKobo.Should().Be(50_000_000_000L);
    }
}
