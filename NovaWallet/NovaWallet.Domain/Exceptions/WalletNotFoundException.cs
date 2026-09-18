namespace NovaWallet.Domain.Exceptions;

public sealed class WalletNotFoundException : DomainException
{
    public WalletNotFoundException(Guid walletId)
        : base($"Wallet '{walletId}' was not found.", "wallet-not-found") { }

    public WalletNotFoundException(string customerId)
        : base($"No wallet found for customer '{customerId}'.", "wallet-not-found") { }
}
