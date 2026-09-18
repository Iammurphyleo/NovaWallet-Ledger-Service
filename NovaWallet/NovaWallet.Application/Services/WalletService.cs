using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaWallet.Application.DTOs.Requests;
using NovaWallet.Application.DTOs.Responses;
using NovaWallet.Application.Interfaces;
using NovaWallet.Application.Settings;
using NovaWallet.Domain.Entities;
using NovaWallet.Domain.Enums;
using NovaWallet.Domain.Exceptions;
using NovaWallet.Domain.Interfaces;

namespace NovaWallet.Application.Services;

public sealed class WalletService : IWalletService
{
    // WAT is UTC+1 with no DST — hardcoded offset is more reliable than IANA name resolution
    private static readonly TimeSpan WatOffset = TimeSpan.FromHours(1);

    private readonly IWalletRepository _walletRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly IIdempotencyRepository _idempotencyRepo;
    private readonly IOutboxRepository _outboxRepo;
    private readonly IUnitOfWork _uow;
    private readonly IValidator<CreateWalletRequest> _createValidator;
    private readonly IValidator<CreditWalletRequest> _creditValidator;
    private readonly IValidator<TransferRequest> _transferValidator;
    private readonly WalletSettings _settings;
    private readonly ILogger<WalletService> _logger;

    public WalletService(
        IWalletRepository walletRepo,
        IAuditLogRepository auditRepo,
        IIdempotencyRepository idempotencyRepo,
        IOutboxRepository outboxRepo,
        IUnitOfWork uow,
        IValidator<CreateWalletRequest> createValidator,
        IValidator<CreditWalletRequest> creditValidator,
        IValidator<TransferRequest> transferValidator,
        IOptions<WalletSettings> settings,
        ILogger<WalletService> logger)
    {
        _walletRepo = walletRepo;
        _auditRepo = auditRepo;
        _idempotencyRepo = idempotencyRepo;
        _outboxRepo = outboxRepo;
        _uow = uow;
        _createValidator = createValidator;
        _creditValidator = creditValidator;
        _transferValidator = transferValidator;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<WalletResponse> CreateWalletAsync(CreateWalletRequest request, CancellationToken ct = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, ct);

        if (await _walletRepo.ExistsByCustomerIdAsync(request.CustomerId, ct))
            throw new WalletAlreadyExistsException(request.CustomerId);

        var wallet = Wallet.Create(request.CustomerId);
        await _walletRepo.AddAsync(wallet, ct);

        await _auditRepo.AddAsync(AuditLog.Create(
            wallet.Id,
            AuditEventType.WalletCreated,
            balanceBeforeKobo: 0,
            balanceAfterKobo: 0), ct);

        await _uow.SaveChangesAsync(ct);

        _logger.LogInformation("Wallet {WalletId} created for customer {CustomerId}", wallet.Id, wallet.CustomerId);

        return MapToWalletResponse(wallet);
    }

    public async Task<BalanceResponse> GetBalanceAsync(Guid walletId, string callerSubject, CancellationToken ct = default)
    {
        var wallet = await RequireWalletAsync(walletId, ct);
        EnforceOwnership(wallet, callerSubject);
        return new BalanceResponse(wallet.Id, wallet.BalanceKobo, wallet.Currency);
    }

    public async Task<TransactionResponse> CreditWalletAsync(
        Guid walletId,
        CreditWalletRequest request,
        string callerSubject,
        CancellationToken ct = default)
    {
        await _creditValidator.ValidateAndThrowAsync(request, ct);

        await _uow.BeginTransactionAsync(ct);
        try
        {
            var wallet = await RequireWalletLockedAsync(walletId, ct);
            EnforceOwnership(wallet, callerSubject);

            var balanceBefore = wallet.BalanceKobo;
            wallet.Credit(request.AmountKobo);

            var tx = WalletTransaction.Create(
                wallet.Id,
                TransactionType.Credit,
                request.AmountKobo,
                balanceBefore,
                wallet.BalanceKobo,
                request.Description);

            await _walletRepo.AddTransactionAsync(tx, ct);
            await _auditRepo.AddAsync(AuditLog.Create(
                wallet.Id,
                AuditEventType.Credit,
                balanceBefore,
                wallet.BalanceKobo,
                request.AmountKobo), ct);

            await _uow.SaveChangesAsync(ct);
            await _uow.CommitTransactionAsync(ct);

            _logger.LogInformation(
                "Wallet {WalletId} credited {AmountKobo} kobo. New balance: {Balance}",
                wallet.Id, request.AmountKobo, wallet.BalanceKobo);

            return MapToTransactionResponse(tx);
        }
        catch
        {
            await _uow.RollbackTransactionAsync(ct);
            throw;
        }
    }

    public async Task<TransferResponse> TransferAsync(
        TransferRequest request,
        string idempotencyKey,
        string callerSubject,
        string correlationId,
        CancellationToken ct = default)
    {
        await _transferValidator.ValidateAndThrowAsync(request, ct);

        var requestHash = ComputeRequestHash(request);

        // Phase 1: Claim the idempotency key atomically (outside the transfer transaction)
        var existing = await _idempotencyRepo.FindAsync(idempotencyKey, ct);
        if (existing is not null)
        {
            if (existing.RequestHash != requestHash)
                throw new IdempotencyKeyConflictException(idempotencyKey);

            if (existing.Status == IdempotencyStatus.Completed && existing.ResponseBody is not null)
            {
                _logger.LogInformation("Idempotent replay for key {Key}", idempotencyKey);
                return JsonSerializer.Deserialize<TransferResponse>(existing.ResponseBody)!;
            }

            // PROCESSING but not by us: another concurrent request claimed it first
            // Treat as stale if old enough, otherwise reject
            if (existing.Status == IdempotencyStatus.Processing
                && DateTime.UtcNow - existing.CreatedAt < TimeSpan.FromSeconds(30))
            {
                throw new IdempotencyKeyInFlightException(idempotencyKey);
            }
        }

        var idempotencyRecord = IdempotencyRecord.CreateProcessing(idempotencyKey, requestHash, request.SourceWalletId);
        var inserted = await _idempotencyRepo.TryInsertAsync(idempotencyRecord, ct);
        if (!inserted)
        {
            // Concurrent request won the INSERT race — re-read and follow the same logic
            var raceWinner = await _idempotencyRepo.FindAsync(idempotencyKey, ct);
            if (raceWinner?.RequestHash != requestHash)
                throw new IdempotencyKeyConflictException(idempotencyKey);

            if (raceWinner?.Status == IdempotencyStatus.Completed && raceWinner.ResponseBody is not null)
                return JsonSerializer.Deserialize<TransferResponse>(raceWinner.ResponseBody)!;

            throw new IdempotencyKeyInFlightException(idempotencyKey);
        }

        // Phase 2: Execute transfer + idempotency update in a single atomic transaction.
        // Keeping the idempotency record update inside the same transaction is critical:
        // it prevents the failure window between "transfer committed" and "idempotency marked COMPLETED"
        // which would otherwise allow a retry to double-debit.
        await _uow.BeginTransactionAsync(ct);
        try
        {
            // Lock both wallets in ascending id order — prevents deadlock
            var (source, destination) = await _walletRepo.LockForTransferAsync(
                request.SourceWalletId, request.DestinationWalletId, ct);

            EnforceOwnership(source, callerSubject);

            // Check daily outbound limit (WAT reset)
            var watMidnightUtc = GetTodayWatMidnightUtc();
            var dailyUsed = await _walletRepo.GetDailyOutboundTotalAsync(source.Id, watMidnightUtc, ct);
            var dailyLimit = _settings.DailyOutboundLimitKobo;

            if (dailyUsed + request.AmountKobo > dailyLimit)
                throw new DailyLimitExceededException(dailyLimit, dailyUsed, request.AmountKobo);

            // Mutate balances through domain methods — these enforce the non-negative invariant
            var srcBalanceBefore = source.BalanceKobo;
            source.Debit(request.AmountKobo); // throws InsufficientFundsException if unable

            var dstBalanceBefore = destination.BalanceKobo;
            destination.Credit(request.AmountKobo);

            var transferId = Guid.NewGuid();

            var srcTx = WalletTransaction.Create(
                source.Id,
                TransactionType.Debit,
                request.AmountKobo,
                srcBalanceBefore,
                source.BalanceKobo,
                request.Description,
                destination.Id,
                idempotencyKey);

            var dstTx = WalletTransaction.Create(
                destination.Id,
                TransactionType.Credit,
                request.AmountKobo,
                dstBalanceBefore,
                destination.BalanceKobo,
                request.Description,
                source.Id,
                idempotencyKey);

            await _walletRepo.AddTransactionAsync(srcTx, ct);
            await _walletRepo.AddTransactionAsync(dstTx, ct);

            await _auditRepo.AddAsync(AuditLog.Create(
                source.Id, AuditEventType.TransferSent,
                srcBalanceBefore, source.BalanceKobo,
                request.AmountKobo, callerSubject, correlationId), ct);

            await _auditRepo.AddAsync(AuditLog.Create(
                destination.Id, AuditEventType.TransferReceived,
                dstBalanceBefore, destination.BalanceKobo,
                request.AmountKobo, null, correlationId), ct);

            var response = new TransferResponse(
                transferId,
                source.Id,
                destination.Id,
                request.AmountKobo,
                source.BalanceKobo,
                idempotencyKey,
                DateTime.UtcNow);

            // Outbox message and idempotency update both in the same transaction.
            var outboxPayload = JsonSerializer.Serialize(new
            {
                transferId,
                sourceWalletId = source.Id,
                destinationWalletId = destination.Id,
                amountKobo = request.AmountKobo,
                correlationId,
                processedAt = DateTime.UtcNow
            });
            await _outboxRepo.AddAsync(OutboxMessage.Create("TransferCompleted", outboxPayload), ct);

            // Mark idempotency key COMPLETED inside this transaction — atomic with the transfer.
            await _idempotencyRepo.CompleteWithinTransactionAsync(
                idempotencyKey,
                responseBody: JsonSerializer.Serialize(response),
                ct);

            await _uow.SaveChangesAsync(ct);
            await _uow.CommitTransactionAsync(ct);

            _logger.LogInformation(
                "Transfer {TransferId}: {Amount} kobo from {Source} to {Dest}. Correlation: {CorrelationId}",
                transferId, request.AmountKobo, source.Id, destination.Id, correlationId);

            return response;
        }
        catch (Exception ex) when (ex is not IdempotencyKeyConflictException
                                        and not IdempotencyKeyInFlightException)
        {
            await _uow.RollbackTransactionAsync(ct);
            // Remove the PROCESSING claim so the client can retry after a business rule failure.
            // Safe to call here because the transfer transaction was rolled back — no money moved.
            await _idempotencyRepo.DeleteProcessingAsync(idempotencyKey, ct);
            throw;
        }
    }

    public async Task<PagedResponse<TransactionResponse>> GetStatementAsync(
        Guid walletId,
        string callerSubject,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var wallet = await RequireWalletAsync(walletId, ct);
        EnforceOwnership(wallet, callerSubject);

        var (items, totalCount) = await _walletRepo.GetTransactionsPagedAsync(walletId, page, pageSize, ct);
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / pageSize);

        return new PagedResponse<TransactionResponse>(
            items.Select(MapToTransactionResponse).ToList(),
            page,
            pageSize,
            totalCount,
            totalPages);
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private async Task<Wallet> RequireWalletAsync(Guid walletId, CancellationToken ct)
    {
        var wallet = await _walletRepo.FindByIdAsync(walletId, ct);
        if (wallet is null) throw new WalletNotFoundException(walletId);
        return wallet;
    }

    private async Task<Wallet> RequireWalletLockedAsync(Guid walletId, CancellationToken ct)
    {
        // For single-wallet operations we lock only that wallet
        var (wallet, _) = await _walletRepo.LockForTransferAsync(walletId, walletId, ct);
        return wallet;
    }

    private static void EnforceOwnership(Wallet wallet, string callerSubject)
    {
        if (!string.Equals(wallet.CustomerId, callerSubject, StringComparison.OrdinalIgnoreCase))
            throw new WalletAccessDeniedException(wallet.Id);
    }

    private static DateTime GetTodayWatMidnightUtc()
    {
        var watNow = DateTime.UtcNow.Add(WatOffset);
        var watMidnight = watNow.Date; // 00:00:00 WAT today
        return watMidnight.Subtract(WatOffset); // back to UTC
    }

    private static string ComputeRequestHash(TransferRequest request)
    {
        var canonical = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static WalletResponse MapToWalletResponse(Wallet w) =>
        new(w.Id, w.CustomerId, w.BalanceKobo, w.Currency, w.CreatedAt);

    private static TransactionResponse MapToTransactionResponse(WalletTransaction t) =>
        new(t.Id, t.Reference, t.Type.ToString(), t.AmountKobo,
            t.BalanceBeforeKobo, t.BalanceAfterKobo, t.Description,
            t.CounterpartWalletId, t.CreatedAt);
}
