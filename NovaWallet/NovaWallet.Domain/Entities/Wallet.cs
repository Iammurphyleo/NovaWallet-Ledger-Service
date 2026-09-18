using NovaWallet.Domain.Exceptions;

namespace NovaWallet.Domain.Entities;

public sealed class Wallet
{
    private long _balanceKobo;

    private Wallet() { } // EF Core constructor

    public static Wallet Create(string customerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        return new Wallet
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            _balanceKobo = 0,
            Currency = "NGN",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public Guid Id { get; private set; }
    public string CustomerId { get; private set; } = default!;

    // EF Core backing field property — long, never float/double
    public long BalanceKobo
    {
        get => _balanceKobo;
        private set => _balanceKobo = value;
    }

    public string Currency { get; private set; } = "NGN";
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public void Credit(long amountKobo)
    {
        if (amountKobo <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountKobo), "Credit amount must be positive.");

        _balanceKobo += amountKobo;
        UpdatedAt = DateTime.UtcNow;
    }

    // Returns balance before debit for audit recording
    public long Debit(long amountKobo)
    {
        if (amountKobo <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountKobo), "Debit amount must be positive.");

        if (_balanceKobo < amountKobo)
            throw new InsufficientFundsException(_balanceKobo, amountKobo);

        var balanceBefore = _balanceKobo;
        _balanceKobo -= amountKobo;
        UpdatedAt = DateTime.UtcNow;
        return balanceBefore;
    }
}
