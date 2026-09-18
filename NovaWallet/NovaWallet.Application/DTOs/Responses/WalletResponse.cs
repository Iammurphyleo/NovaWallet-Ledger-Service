namespace NovaWallet.Application.DTOs.Responses;

public sealed record WalletResponse(
    Guid Id,
    string CustomerId,
    long BalanceKobo,
    string Currency,
    DateTime CreatedAt);
