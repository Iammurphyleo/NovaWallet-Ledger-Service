using NovaWallet.Domain.Entities;

namespace NovaWallet.Domain.Interfaces;

public interface IOutboxRepository
{
    Task AddAsync(OutboxMessage message, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken ct = default);
    Task MarkPublishedAsync(Guid messageId, CancellationToken ct = default);
}
