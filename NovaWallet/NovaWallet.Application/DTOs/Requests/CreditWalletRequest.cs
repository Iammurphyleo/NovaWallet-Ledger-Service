namespace NovaWallet.Application.DTOs.Requests;

public sealed record CreditWalletRequest(
    long AmountKobo,
    string? Description = null);
