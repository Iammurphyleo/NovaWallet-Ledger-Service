namespace NovaWallet.Application.DTOs.Requests;

public sealed record TransferRequest(
    Guid SourceWalletId,
    Guid DestinationWalletId,
    long AmountKobo,
    string? Description = null);
