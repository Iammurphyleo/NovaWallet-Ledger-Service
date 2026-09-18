namespace NovaWallet.Domain.Exceptions;

public sealed class DailyLimitExceededException : DomainException
{
    public DailyLimitExceededException(long limitKobo, long usedKobo, long requestedKobo)
        : base(
            $"Daily outbound limit of {limitKobo} kobo exceeded. Used: {usedKobo} kobo, Requested: {requestedKobo} kobo.",
            "daily-limit-exceeded")
    { }
}
