using NovaWallet.Domain.Enums;

namespace NovaWallet.Domain.Entities;

public sealed class WalletTransaction
{
    private WalletTransaction() { } // EF Core constructor

    public static WalletTransaction Create(
        Guid walletId,
        TransactionType type,
        long amountKobo,
        long balanceBeforeKobo,
        long balanceAfterKobo,
        string? description = null,
        Guid? counterpartWalletId = null,
        string? idempotencyKey = null)
    {
        return new WalletTransaction
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            Reference = Guid.NewGuid().ToString("N"),
            Type = type,
            AmountKobo = amountKobo,
            BalanceBeforeKobo = balanceBeforeKobo,
            BalanceAfterKobo = balanceAfterKobo,
            Description = description,
            CounterpartWalletId = counterpartWalletId,
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTime.UtcNow
        };
    }

    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }
    public string Reference { get; private set; } = default!;
    public TransactionType Type { get; private set; }
    public long AmountKobo { get; private set; }
    public long BalanceBeforeKobo { get; private set; }
    public long BalanceAfterKobo { get; private set; }
    public string? Description { get; private set; }
    public Guid? CounterpartWalletId { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public DateTime CreatedAt { get; private set; }
}
