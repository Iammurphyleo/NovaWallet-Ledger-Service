namespace NovaWallet.Domain.Exceptions;

public sealed class IdempotencyKeyInFlightException : DomainException
{
    public IdempotencyKeyInFlightException(string key)
        : base(
            $"A request with idempotency key '{key}' is currently being processed.",
            "idempotency-in-flight")
    { }
}
