using Microsoft.EntityFrameworkCore;
using NovaWallet.Domain.Entities;
using NovaWallet.Domain.Enums;
using NovaWallet.Domain.Exceptions;
using NovaWallet.Domain.Interfaces;
using NovaWallet.Infrastructure.Data;

namespace NovaWallet.Infrastructure.Repositories;

public sealed class WalletRepository : IWalletRepository
{
    private readonly NovaWalletDbContext _db;

    public WalletRepository(NovaWalletDbContext db) => _db = db;

    public Task<Wallet?> FindByIdAsync(Guid walletId, CancellationToken ct) =>
        _db.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, ct);

    public Task<Wallet?> FindByCustomerIdAsync(string customerId, CancellationToken ct) =>
        _db.Wallets.FirstOrDefaultAsync(w => w.CustomerId == customerId, ct);

    public Task<bool> ExistsByCustomerIdAsync(string customerId, CancellationToken ct) =>
        _db.Wallets.AnyAsync(w => w.CustomerId == customerId, ct);

    public async Task AddAsync(Wallet wallet, CancellationToken ct) =>
        await _db.Wallets.AddAsync(wallet, ct);

    public async Task<(Wallet source, Wallet destination)> LockForTransferAsync(
        Guid sourceId, Guid destinationId, CancellationToken ct)
    {
        // Lock both rows in ascending id order to prevent deadlock when A→B and B→A happen concurrently.
        // FromSqlInterpolated returns tracked entities so EF Core will generate UPDATE statements for them.
        List<Wallet> wallets;

        if (sourceId == destinationId)
        {
            // Single-wallet operation (e.g. credit): lock just the one row
            wallets = await _db.Wallets
                .FromSqlInterpolated($"SELECT * FROM wallets WHERE id = {sourceId} FOR UPDATE")
                .AsTracking()
                .ToListAsync(ct);
        }
        else
        {
            wallets = await _db.Wallets
                .FromSqlInterpolated(
                    $"SELECT * FROM wallets WHERE id IN ({sourceId}, {destinationId}) ORDER BY id FOR UPDATE")
                .AsTracking()
                .ToListAsync(ct);
        }

        var source = wallets.FirstOrDefault(w => w.Id == sourceId)
            ?? throw new WalletNotFoundException(sourceId);

        var destination = sourceId == destinationId
            ? source
            : (wallets.FirstOrDefault(w => w.Id == destinationId)
               ?? throw new WalletNotFoundException(destinationId));

        return (source, destination);
    }

    public async Task<long> GetDailyOutboundTotalAsync(Guid walletId, DateTime fromUtc, CancellationToken ct)
    {
        var result = await _db.WalletTransactions
            .Where(t => t.WalletId == walletId
                     && t.Type == TransactionType.Debit
                     && t.CreatedAt >= fromUtc)
            .SumAsync(t => (long?)t.AmountKobo, ct);

        return result ?? 0L;
    }

    public async Task AddTransactionAsync(WalletTransaction transaction, CancellationToken ct) =>
        await _db.WalletTransactions.AddAsync(transaction, ct);

    public async Task<(IReadOnlyList<WalletTransaction> items, int totalCount)> GetTransactionsPagedAsync(
        Guid walletId, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.WalletTransactions
            .Where(t => t.WalletId == walletId)
            .OrderByDescending(t => t.CreatedAt);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}
