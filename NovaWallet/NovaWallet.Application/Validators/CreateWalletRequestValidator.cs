using FluentValidation;
using NovaWallet.Application.DTOs.Requests;

namespace NovaWallet.Application.Validators;

public sealed class CreateWalletRequestValidator : AbstractValidator<CreateWalletRequest>
{
    public CreateWalletRequestValidator()
    {
        RuleFor(x => x.CustomerId)
            .NotEmpty().WithMessage("CustomerId is required.")
            .MaximumLength(100).WithMessage("CustomerId must not exceed 100 characters.")
            .Matches(@"^[a-zA-Z0-9_\-]+$").WithMessage("CustomerId may only contain letters, digits, underscores, and hyphens.");
    }
}
