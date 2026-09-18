namespace NovaWallet.Presentation.DTOs;

public sealed record TokenResponse(string Token, DateTime ExpiresAt, string CustomerId);
