using System.ComponentModel.DataAnnotations;

namespace NovaWallet.Presentation.DTOs;

public sealed record StatementQuery
{
    [Range(1, int.MaxValue, ErrorMessage = "page must be ≥ 1.")]
    public int Page { get; init; } = 1;

    [Range(1, 100, ErrorMessage = "pageSize must be between 1 and 100.")]
    public int PageSize { get; init; } = 20;
}
