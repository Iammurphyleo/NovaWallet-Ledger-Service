using System.ComponentModel.DataAnnotations;

namespace NovaWallet.Presentation.DTOs;

public sealed record TokenRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "CustomerId is required.")]
    public string? CustomerId { get; init; }
}
