namespace NovaWallet.Domain.Exceptions;

public sealed class InsufficientFundsException : DomainException
{
    public InsufficientFundsException(long balanceKobo, long requiredKobo)
        : base(
            $"Insufficient funds. Available: {balanceKobo} kobo, Required: {requiredKobo} kobo.",
            "insufficient-funds")
    { }
}
