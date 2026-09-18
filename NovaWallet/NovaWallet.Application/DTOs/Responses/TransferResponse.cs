namespace NovaWallet.Application.DTOs.Responses;

public sealed record TransferResponse(
    Guid TransferId,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    long AmountKobo,
    long SourceBalanceAfterKobo,
    string IdempotencyKey,
    DateTime ProcessedAt);
