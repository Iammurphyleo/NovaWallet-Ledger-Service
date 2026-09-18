using NovaWallet.Application.DTOs.Requests;
using NovaWallet.Application.DTOs.Responses;

namespace NovaWallet.Application.Interfaces;

public interface IWalletService
{
    Task<WalletResponse> CreateWalletAsync(CreateWalletRequest request, CancellationToken ct = default);

    Task<BalanceResponse> GetBalanceAsync(Guid walletId, string callerSubject, CancellationToken ct = default);

    Task<TransactionResponse> CreditWalletAsync(
        Guid walletId,
        CreditWalletRequest request,
        string callerSubject,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically transfers funds. Enforces: no negative balance, daily limit, idempotency.
    /// <paramref name="idempotencyKey"/> is required.
    /// </summary>
    Task<TransferResponse> TransferAsync(
        TransferRequest request,
        string idempotencyKey,
        string callerSubject,
        string correlationId,
        CancellationToken ct = default);

    Task<PagedResponse<TransactionResponse>> GetStatementAsync(
        Guid walletId,
        string callerSubject,
        int page,
        int pageSize,
        CancellationToken ct = default);
}
