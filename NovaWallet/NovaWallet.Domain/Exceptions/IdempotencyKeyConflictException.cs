namespace NovaWallet.Domain.Exceptions;

public sealed class IdempotencyKeyConflictException : DomainException
{
    public IdempotencyKeyConflictException(string key)
        : base(
            $"Idempotency key '{key}' was already used with a different request payload.",
            "idempotency-key-conflict")
    { }
}
