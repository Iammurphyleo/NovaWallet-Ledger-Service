using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NovaWallet.Domain.Interfaces;

namespace NovaWallet.Infrastructure.Data;

public sealed class UnitOfWork : IUnitOfWork
{
    private readonly NovaWalletDbContext _db;
    private IDbContextTransaction? _transaction;

    public UnitOfWork(NovaWalletDbContext db) => _db = db;

    public Task<int> SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);

    public async Task BeginTransactionAsync(CancellationToken ct)
    {
        _transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
    }

    public async Task CommitTransactionAsync(CancellationToken ct)
    {
        if (_transaction is null) throw new InvalidOperationException("No active transaction.");
        await _transaction.CommitAsync(ct);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    public async Task RollbackTransactionAsync(CancellationToken ct)
    {
        if (_transaction is null) return;
        await _transaction.RollbackAsync(ct);
        await _transaction.DisposeAsync();
        _transaction = null;
    }
}
