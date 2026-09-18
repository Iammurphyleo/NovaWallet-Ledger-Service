using FluentValidation;
using NovaWallet.Application.DTOs.Requests;

namespace NovaWallet.Application.Validators;

public sealed class TransferRequestValidator : AbstractValidator<TransferRequest>
{
    // Max single transfer: ₦5,000,000 = 500,000,000 kobo
    private const long MaxSingleTransferKobo = 500_000_000L;

    public TransferRequestValidator()
    {
        RuleFor(x => x.SourceWalletId)
            .NotEmpty().WithMessage("SourceWalletId is required.");

        RuleFor(x => x.DestinationWalletId)
            .NotEmpty().WithMessage("DestinationWalletId is required.")
            .NotEqual(x => x.SourceWalletId).WithMessage("Source and destination wallets must be different.");

        RuleFor(x => x.AmountKobo)
            .GreaterThan(0).WithMessage("Amount must be greater than zero.")
            .LessThanOrEqualTo(MaxSingleTransferKobo)
                .WithMessage($"Single transfer cannot exceed {MaxSingleTransferKobo} kobo.");

        RuleFor(x => x.Description)
            .MaximumLength(500).WithMessage("Description must not exceed 500 characters.")
            .When(x => x.Description is not null);
    }
}
