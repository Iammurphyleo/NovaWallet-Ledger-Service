using NovaWallet.Domain.Enums;

namespace NovaWallet.Domain.Entities;

// Immutable after creation — the only valid operation is Insert.
// In production, the DB role used by the service would have INSERT-only on this table.
public sealed class AuditLog
{
    private AuditLog() { } // EF Core constructor

    public static AuditLog Create(
        Guid walletId,
        AuditEventType eventType,
        long balanceBeforeKobo,
        long balanceAfterKobo,
        long? amountKobo = null,
        string? actorSubject = null,
        string? correlationId = null,
        object? metadata = null)
    {
        return new AuditLog
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            EventType = eventType,
            AmountKobo = amountKobo,
            BalanceBeforeKobo = balanceBeforeKobo,
            BalanceAfterKobo = balanceAfterKobo,
            ActorSubject = actorSubject,
            CorrelationId = correlationId,
            CreatedAt = DateTime.UtcNow
        };
    }

    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }
    public AuditEventType EventType { get; private set; }
    public long? AmountKobo { get; private set; }
    public long BalanceBeforeKobo { get; private set; }
    public long BalanceAfterKobo { get; private set; }
    public string? ActorSubject { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTime CreatedAt { get; private set; }
}
