namespace NovaWallet.Application.DTOs.Responses;

public sealed record TransactionResponse(
    Guid Id,
    string Reference,
    string Type,
    long AmountKobo,
    long BalanceBeforeKobo,
    long BalanceAfterKobo,
    string? Description,
    Guid? CounterpartWalletId,
    DateTime CreatedAt);
