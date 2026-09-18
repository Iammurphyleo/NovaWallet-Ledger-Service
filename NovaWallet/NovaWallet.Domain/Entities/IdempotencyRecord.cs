namespace NovaWallet.Domain.Entities;

public sealed class IdempotencyRecord
{
    private IdempotencyRecord() { } // EF Core constructor

    public static IdempotencyRecord CreateProcessing(string key, string requestHash, Guid walletId)
    {
        var now = DateTime.UtcNow;
        return new IdempotencyRecord
        {
            Key = key,
            RequestHash = requestHash,
            WalletId = walletId,
            Status = IdempotencyStatus.Processing,
            ResponseStatus = 0,
            ResponseBody = null,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24)
        };
    }

    public string Key { get; private set; } = default!;
    public string RequestHash { get; private set; } = default!;
    public Guid WalletId { get; private set; }
    public IdempotencyStatus Status { get; set; }
    public int ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
}

public enum IdempotencyStatus
{
    Processing,
    Completed
}
