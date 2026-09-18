namespace NovaWallet.Domain.Exceptions;

public sealed class WalletAccessDeniedException : DomainException
{
    public WalletAccessDeniedException(Guid walletId)
        : base($"Access to wallet '{walletId}' is denied.", "wallet-access-denied") { }
}
