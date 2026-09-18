namespace NovaWallet.Application.Settings;

public sealed class WalletSettings
{
    public const string SectionName = "Wallet";

    // Default: ₦500,000/day = 50,000,000 kobo
    public long DailyOutboundLimitKobo { get; init; } = 50_000_000L;
}
