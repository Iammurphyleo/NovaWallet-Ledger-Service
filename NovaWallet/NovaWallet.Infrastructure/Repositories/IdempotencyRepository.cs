using Microsoft.EntityFrameworkCore;
using NovaWallet.Domain.Entities;
using NovaWallet.Domain.Interfaces;
using NovaWallet.Infrastructure.Data;

namespace NovaWallet.Infrastructure.Repositories;

public sealed class IdempotencyRepository : IIdempotencyRepository
{
    private readonly NovaWalletDbContext _db;

    public IdempotencyRepository(NovaWalletDbContext db) => _db = db;

    public async Task<bool> TryInsertAsync(IdempotencyRecord record, CancellationToken ct)
    {
        // Use raw SQL with ON CONFLICT DO NOTHING for an atomic check-and-insert.
        // This avoids the TOCTOU race between checking existence and inserting.
        var rows = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO idempotency_records (key, request_hash, wallet_id, status, response_status, response_body, created_at, expires_at)
            VALUES ({record.Key}, {record.RequestHash}, {record.WalletId}, {record.Status.ToString()}, {record.ResponseStatus}, {record.ResponseBody}::jsonb, {record.CreatedAt}, {record.ExpiresAt})
            ON CONFLICT (key) DO NOTHING
            """,
            ct);

        return rows == 1;
    }

    public Task<IdempotencyRecord?> FindAsync(string key, CancellationToken ct) =>
        _db.IdempotencyRecords.FirstOrDefaultAsync(r => r.Key == key, ct);

    public async Task UpdateAsync(IdempotencyRecord record, CancellationToken ct)
    {
        _db.IdempotencyRecords.Update(record);
        await _db.SaveChangesAsync(ct);
    }

    public async Task CompleteWithinTransactionAsync(string key, string responseBody, CancellationToken ct)
    {
        // Runs inside the caller's open transaction — uses the shared DbContext connection/transaction.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE idempotency_records
            SET status = 'Completed',
                response_status = 200,
                response_body = {responseBody}::jsonb
            WHERE key = {key}
            """,
            ct);
    }

    public async Task DeleteProcessingAsync(string key, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM idempotency_records WHERE key = {key} AND status = 'Processing'",
            ct);
    }
}
