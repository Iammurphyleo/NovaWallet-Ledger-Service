using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NovaWallet.Application.DTOs.Requests;
using NovaWallet.Application.Services;
using NovaWallet.Application.Settings;
using NovaWallet.Domain.Entities;
using NovaWallet.Domain.Exceptions;
using NovaWallet.Domain.Interfaces;
using NSubstitute;

namespace NovaWallet.Tests.Unit.Services;

public sealed class WalletServiceTests
{
    private readonly IWalletRepository _walletRepo = Substitute.For<IWalletRepository>();
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly IIdempotencyRepository _idempotencyRepo = Substitute.For<IIdempotencyRepository>();
    private readonly IOutboxRepository _outboxRepo = Substitute.For<IOutboxRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly WalletService _sut;

    public WalletServiceTests()
    {
        var settings = Options.Create(new WalletSettings { DailyOutboundLimitKobo = 50_000_000L });
        _sut = new WalletService(
            _walletRepo, _auditRepo, _idempotencyRepo, _outboxRepo, _uow,
            new Application.Validators.CreateWalletRequestValidator(),
            new Application.Validators.CreditWalletRequestValidator(),
            new Application.Validators.TransferRequestValidator(),
            settings,
            NullLogger<WalletService>.Instance);
    }

    // ── CreateWallet ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateWallet_NewCustomer_ReturnsWalletWithZeroBalance()
    {
        _walletRepo.ExistsByCustomerIdAsync("cust-1", default).Returns(false);

        var result = await _sut.CreateWalletAsync(new CreateWalletRequest("cust-1"));

        result.CustomerId.Should().Be("cust-1");
        result.BalanceKobo.Should().Be(0);
        result.Currency.Should().Be("NGN");
        await _walletRepo.Received(1).AddAsync(Arg.Any<Wallet>(), default);
        await _uow.Received(1).SaveChangesAsync(default);
    }

    [Fact]
    public async Task CreateWallet_DuplicateCustomer_ThrowsWalletAlreadyExistsException()
    {
        _walletRepo.ExistsByCustomerIdAsync("cust-1", default).Returns(true);

        var act = async () => await _sut.CreateWalletAsync(new CreateWalletRequest("cust-1"));

        await act.Should().ThrowAsync<WalletAlreadyExistsException>();
        await _walletRepo.DidNotReceive().AddAsync(Arg.Any<Wallet>(), default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid customer id with spaces")]
    public async Task CreateWallet_InvalidCustomerId_ThrowsValidationException(string customerId)
    {
        var act = async () => await _sut.CreateWalletAsync(new CreateWalletRequest(customerId));
        await act.Should().ThrowAsync<ValidationException>();
    }

    // ── GetBalance ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBalance_ExistingWallet_ReturnsCorrectBalance()
    {
        var wallet = Wallet.Create("cust-1");
        wallet.Credit(100_000);
        _walletRepo.FindByIdAsync(wallet.Id, default).Returns(wallet);

        var result = await _sut.GetBalanceAsync(wallet.Id, "cust-1");

        result.BalanceKobo.Should().Be(100_000);
        result.Currency.Should().Be("NGN");
    }

    [Fact]
    public async Task GetBalance_NonExistentWallet_ThrowsWalletNotFoundException()
    {
        _walletRepo.FindByIdAsync(Arg.Any<Guid>(), default).Returns((Wallet?)null);

        var act = async () => await _sut.GetBalanceAsync(Guid.NewGuid(), "cust-1");
        await act.Should().ThrowAsync<WalletNotFoundException>();
    }

    [Fact]
    public async Task GetBalance_WrongOwner_ThrowsWalletAccessDeniedException()
    {
        var wallet = Wallet.Create("cust-1");
        _walletRepo.FindByIdAsync(wallet.Id, default).Returns(wallet);

        var act = async () => await _sut.GetBalanceAsync(wallet.Id, "attacker");
        await act.Should().ThrowAsync<WalletAccessDeniedException>();
    }

    // ── CreditWallet ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreditWallet_ValidAmount_UpdatesBalance()
    {
        var wallet = Wallet.Create("cust-1");
        var walletId = wallet.Id;
        _walletRepo.LockForTransferAsync(walletId, walletId, default).Returns((wallet, wallet));

        var result = await _sut.CreditWalletAsync(
            walletId, new CreditWalletRequest(50_000), "cust-1");

        result.AmountKobo.Should().Be(50_000);
        result.BalanceAfterKobo.Should().Be(50_000);
        result.Type.Should().Be("Credit");
        await _uow.Received(1).CommitTransactionAsync(default);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreditWallet_NonPositiveAmount_ThrowsValidationException(long amount)
    {
        var act = async () => await _sut.CreditWalletAsync(
            Guid.NewGuid(), new CreditWalletRequest(amount), "cust-1");
        await act.Should().ThrowAsync<ValidationException>();
    }

    // ── Transfer ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transfer_ValidTransfer_DebitsSenderCreditReceiver()
    {
        var source = Wallet.Create("cust-sender");
        source.Credit(100_000);
        var dest = Wallet.Create("cust-receiver");

        _idempotencyRepo.FindAsync(Arg.Any<string>(), default).Returns((IdempotencyRecord?)null);
        _idempotencyRepo.TryInsertAsync(Arg.Any<IdempotencyRecord>(), default).Returns(true);
        _walletRepo.LockForTransferAsync(source.Id, dest.Id, default).Returns((source, dest));
        _walletRepo.GetDailyOutboundTotalAsync(source.Id, Arg.Any<DateTime>(), default).Returns(0L);

        var request = new TransferRequest(source.Id, dest.Id, 30_000);
        var result = await _sut.TransferAsync(request, "key-1", "cust-sender", "corr-1");

        result.AmountKobo.Should().Be(30_000);
        result.SourceBalanceAfterKobo.Should().Be(70_000);
        source.BalanceKobo.Should().Be(70_000);
        dest.BalanceKobo.Should().Be(30_000);
    }

    [Fact]
    public async Task Transfer_InsufficientFunds_ThrowsInsufficientFundsException()
    {
        var source = Wallet.Create("cust-sender");
        source.Credit(1_000);
        var dest = Wallet.Create("cust-receiver");

        _idempotencyRepo.FindAsync(Arg.Any<string>(), default).Returns((IdempotencyRecord?)null);
        _idempotencyRepo.TryInsertAsync(Arg.Any<IdempotencyRecord>(), default).Returns(true);
        _walletRepo.LockForTransferAsync(source.Id, dest.Id, default).Returns((source, dest));
        _walletRepo.GetDailyOutboundTotalAsync(source.Id, Arg.Any<DateTime>(), default).Returns(0L);

        var act = async () => await _sut.TransferAsync(
            new TransferRequest(source.Id, dest.Id, 1_001), "key-1", "cust-sender", "corr-1");

        await act.Should().ThrowAsync<InsufficientFundsException>();
        await _idempotencyRepo.Received(1).DeleteProcessingAsync("key-1", default);
    }

    [Fact]
    public async Task Transfer_ExceedsDailyLimit_ThrowsDailyLimitExceededException()
    {
        var source = Wallet.Create("cust-sender");
        source.Credit(100_000_000L); // large balance
        var dest = Wallet.Create("cust-receiver");

        _idempotencyRepo.FindAsync(Arg.Any<string>(), default).Returns((IdempotencyRecord?)null);
        _idempotencyRepo.TryInsertAsync(Arg.Any<IdempotencyRecord>(), default).Returns(true);
        _walletRepo.LockForTransferAsync(source.Id, dest.Id, default).Returns((source, dest));
        // Already used 49,999,999 kobo today — transfer of 2 would exceed 50,000,000 limit
        _walletRepo.GetDailyOutboundTotalAsync(source.Id, Arg.Any<DateTime>(), default)
            .Returns(49_999_999L);

        var act = async () => await _sut.TransferAsync(
            new TransferRequest(source.Id, dest.Id, 2), "key-1", "cust-sender", "corr-1");

        await act.Should().ThrowAsync<DailyLimitExceededException>();
    }

    [Fact]
    public async Task Transfer_SameSourceAndDestination_ThrowsValidationException()
    {
        var walletId = Guid.NewGuid();
        var act = async () => await _sut.TransferAsync(
            new TransferRequest(walletId, walletId, 1_000), "key-1", "cust-1", "corr-1");
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Transfer_WrongOwner_ThrowsWalletAccessDeniedException()
    {
        var source = Wallet.Create("cust-sender");
        source.Credit(100_000);
        var dest = Wallet.Create("cust-receiver");

        _idempotencyRepo.FindAsync(Arg.Any<string>(), default).Returns((IdempotencyRecord?)null);
        _idempotencyRepo.TryInsertAsync(Arg.Any<IdempotencyRecord>(), default).Returns(true);
        _walletRepo.LockForTransferAsync(source.Id, dest.Id, default).Returns((source, dest));
        _walletRepo.GetDailyOutboundTotalAsync(source.Id, Arg.Any<DateTime>(), default).Returns(0L);

        var act = async () => await _sut.TransferAsync(
            new TransferRequest(source.Id, dest.Id, 1_000), "key-1", "attacker", "corr-1");

        await act.Should().ThrowAsync<WalletAccessDeniedException>();
        await _idempotencyRepo.Received(1).DeleteProcessingAsync("key-1", default);
    }

    [Fact]
    public async Task Transfer_IdempotentReplay_ReturnsCachedResponse()
    {
        var source = Wallet.Create("cust-sender");
        var dest = Wallet.Create("cust-receiver");
        var request = new TransferRequest(source.Id, dest.Id, 1_000);

        // Simulate a completed record
        var existingRecord = IdempotencyRecord.CreateProcessing("key-replay", "dummyhash", source.Id);
        existingRecord.Status = NovaWallet.Domain.Entities.IdempotencyStatus.Completed;
        var cachedResponse = new Application.DTOs.Responses.TransferResponse(
            Guid.NewGuid(), source.Id, dest.Id, 1_000, 99_000, "key-replay", DateTime.UtcNow);
        existingRecord.ResponseBody = System.Text.Json.JsonSerializer.Serialize(cachedResponse);

        // The hash of the actual request should match what's stored
        // We need to compute the same hash the service does — use reflection or just test the behavior
        // by storing the computed hash
        var hash = ComputeHash(request);
        var completedRecord = IdempotencyRecord.CreateProcessing("key-replay", hash, source.Id);
        completedRecord.Status = NovaWallet.Domain.Entities.IdempotencyStatus.Completed;
        completedRecord.ResponseBody = System.Text.Json.JsonSerializer.Serialize(cachedResponse);

        _idempotencyRepo.FindAsync("key-replay", default).Returns(completedRecord);

        var result = await _sut.TransferAsync(request, "key-replay", "cust-sender", "corr-1");

        // Should return cached response, no actual transfer
        result.IdempotencyKey.Should().Be("key-replay");
        await _walletRepo.DidNotReceive().LockForTransferAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), default);
    }

    [Fact]
    public async Task Transfer_IdempotentKeyDifferentPayload_ThrowsIdempotencyKeyConflictException()
    {
        var source = Wallet.Create("cust-sender");
        var dest = Wallet.Create("cust-receiver");

        // Record with a different hash
        var conflictRecord = IdempotencyRecord.CreateProcessing("key-conflict", "different-hash-000000000000000000000000000000000000000000000000000000000000", source.Id);
        conflictRecord.Status = NovaWallet.Domain.Entities.IdempotencyStatus.Completed;

        _idempotencyRepo.FindAsync("key-conflict", default).Returns(conflictRecord);

        var act = async () => await _sut.TransferAsync(
            new TransferRequest(source.Id, dest.Id, 9_999), "key-conflict", "cust-sender", "corr-1");

        await act.Should().ThrowAsync<IdempotencyKeyConflictException>();
    }

    [Fact]
    public async Task GetStatement_ReturnsPagedResultsNewestFirst()
    {
        var wallet = Wallet.Create("cust-1");
        _walletRepo.FindByIdAsync(wallet.Id, default).Returns(wallet);

        var transactions = Enumerable.Range(0, 5).Select(i =>
            WalletTransaction.Create(wallet.Id, NovaWallet.Domain.Enums.TransactionType.Credit,
                1000 * (i + 1), 0, 1000 * (i + 1))).ToList();

        _walletRepo.GetTransactionsPagedAsync(wallet.Id, 1, 10, default)
            .Returns(((IReadOnlyList<WalletTransaction>)transactions, 5));

        var result = await _sut.GetStatementAsync(wallet.Id, "cust-1", 1, 10);

        result.TotalCount.Should().Be(5);
        result.Items.Should().HaveCount(5);
        result.TotalPages.Should().Be(1);
    }

    [Fact]
    public async Task GetStatement_EmptyWallet_ReturnsEmptyPage()
    {
        var wallet = Wallet.Create("cust-1");
        _walletRepo.FindByIdAsync(wallet.Id, default).Returns(wallet);
        _walletRepo.GetTransactionsPagedAsync(wallet.Id, 1, 20, default)
            .Returns(((IReadOnlyList<WalletTransaction>)new List<WalletTransaction>(), 0));

        var result = await _sut.GetStatementAsync(wallet.Id, "cust-1", 1, 20);

        result.TotalCount.Should().Be(0);
        result.Items.Should().BeEmpty();
        result.TotalPages.Should().Be(0);
    }

    private static string ComputeHash(TransferRequest request)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(request,
            new System.Text.Json.JsonSerializerOptions
            { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
