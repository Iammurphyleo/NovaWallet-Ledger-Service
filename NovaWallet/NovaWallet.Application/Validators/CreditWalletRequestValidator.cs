using FluentValidation;
using NovaWallet.Application.DTOs.Requests;

namespace NovaWallet.Application.Validators;

public sealed class CreditWalletRequestValidator : AbstractValidator<CreditWalletRequest>
{
    // Max single credit: ₦50,000,000 = 5,000,000,000 kobo (5 billion)
    private const long MaxSingleCreditKobo = 5_000_000_000L;

    public CreditWalletRequestValidator()
    {
        RuleFor(x => x.AmountKobo)
            .GreaterThan(0).WithMessage("Amount must be greater than zero.")
            .LessThanOrEqualTo(MaxSingleCreditKobo)
                .WithMessage($"Single credit cannot exceed {MaxSingleCreditKobo} kobo.");

        RuleFor(x => x.Description)
            .MaximumLength(500).WithMessage("Description must not exceed 500 characters.")
            .When(x => x.Description is not null);
    }
}
