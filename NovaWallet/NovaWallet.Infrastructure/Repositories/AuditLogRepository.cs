using NovaWallet.Domain.Entities;
using NovaWallet.Domain.Interfaces;
using NovaWallet.Infrastructure.Data;

namespace NovaWallet.Infrastructure.Repositories;

public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly NovaWalletDbContext _db;

    public AuditLogRepository(NovaWalletDbContext db) => _db = db;

    public async Task AddAsync(AuditLog auditLog, CancellationToken ct) =>
        await _db.AuditLogs.AddAsync(auditLog, ct);
}
