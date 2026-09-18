using Microsoft.EntityFrameworkCore;
using NovaWallet.Domain.Entities;
using NovaWallet.Domain.Interfaces;
using NovaWallet.Infrastructure.Data;

namespace NovaWallet.Infrastructure.Repositories;

public sealed class OutboxRepository : IOutboxRepository
{
    private readonly NovaWalletDbContext _db;

    public OutboxRepository(NovaWalletDbContext db) => _db = db;

    public async Task AddAsync(OutboxMessage message, CancellationToken ct) =>
        await _db.OutboxMessages.AddAsync(message, ct);

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken ct) =>
        _db.OutboxMessages
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct)
            .ContinueWith<IReadOnlyList<OutboxMessage>>(t => t.Result, ct);

    public async Task MarkPublishedAsync(Guid messageId, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE outbox_messages SET published_at = {DateTime.UtcNow} WHERE id = {messageId}",
            ct);
    }
}
