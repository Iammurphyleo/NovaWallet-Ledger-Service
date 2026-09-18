namespace NovaWallet.Domain.Exceptions;

public sealed class WalletAlreadyExistsException : DomainException
{
    public WalletAlreadyExistsException(string customerId)
        : base($"A wallet for customer '{customerId}' already exists.", "wallet-already-exists") { }
}
