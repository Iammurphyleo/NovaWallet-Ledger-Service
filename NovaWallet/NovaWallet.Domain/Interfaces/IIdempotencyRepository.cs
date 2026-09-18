using NovaWallet.Domain.Entities;

namespace NovaWallet.Domain.Interfaces;

public interface IIdempotencyRepository
{
    /// <summary>
    /// Atomically inserts the record. Returns false if the key already exists.
    /// </summary>
    Task<bool> TryInsertAsync(IdempotencyRecord record, CancellationToken ct = default);

    Task<IdempotencyRecord?> FindAsync(string key, CancellationToken ct = default);
    Task UpdateAsync(IdempotencyRecord record, CancellationToken ct = default);

    /// <summary>
    /// Marks the key COMPLETED inside the caller's open transaction.
    /// Must be called before SaveChangesAsync/Commit.
    /// </summary>
    Task CompleteWithinTransactionAsync(string key, string responseBody, CancellationToken ct = default);

    /// <summary>
    /// Removes a PROCESSING record on failure so the client can retry.
    /// </summary>
    Task DeleteProcessingAsync(string key, CancellationToken ct = default);
}
