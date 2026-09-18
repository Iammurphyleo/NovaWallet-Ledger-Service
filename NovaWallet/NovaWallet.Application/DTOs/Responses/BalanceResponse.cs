namespace NovaWallet.Application.DTOs.Responses;

public sealed record BalanceResponse(
    Guid WalletId,
    long BalanceKobo,
    string Currency);
