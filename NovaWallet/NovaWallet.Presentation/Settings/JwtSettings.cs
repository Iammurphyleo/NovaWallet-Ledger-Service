namespace NovaWallet.Presentation.Settings;

public sealed class JwtSettings
{
    public const string SectionName = "Jwt";

    public string SigningKey { get; init; } = string.Empty;
    public string Issuer { get; init; } = "novawallet-mock";
    public string Audience { get; init; } = "novawallet-api";
    public int ExpiryMinutes { get; init; } = 60;
}
