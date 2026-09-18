using NovaWallet.Domain.Entities;

namespace NovaWallet.Domain.Interfaces;

public interface IWalletRepository
{
    Task<Wallet?> FindByIdAsync(Guid walletId, CancellationToken ct = default);
    Task<Wallet?> FindByCustomerIdAsync(string customerId, CancellationToken ct = default);
    Task<bool> ExistsByCustomerIdAsync(string customerId, CancellationToken ct = default);
    Task AddAsync(Wallet wallet, CancellationToken ct = default);

    /// <summary>
    /// Lock both wallet rows FOR UPDATE in ascending id order (deadlock-safe),
    /// then return them. Caller must be inside an open transaction.
    /// </summary>
    Task<(Wallet source, Wallet destination)> LockForTransferAsync(
        Guid sourceId, Guid destinationId, CancellationToken ct = default);

    /// <summary>
    /// Sum of all DEBIT transactions for the wallet on or after <paramref name="fromUtc"/>.
    /// Must be called inside the same transaction as the balance update.
    /// </summary>
    Task<long> GetDailyOutboundTotalAsync(Guid walletId, DateTime fromUtc, CancellationToken ct = default);

    Task AddTransactionAsync(WalletTransaction transaction, CancellationToken ct = default);
    Task<(IReadOnlyList<WalletTransaction> items, int totalCount)> GetTransactionsPagedAsync(
        Guid walletId, int page, int pageSize, CancellationToken ct = default);
}
