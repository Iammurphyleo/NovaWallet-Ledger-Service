using NovaWallet.Domain.Entities;

namespace NovaWallet.Domain.Interfaces;

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog auditLog, CancellationToken ct = default);
}
