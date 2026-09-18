using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NovaWallet.Application.DTOs.Requests;
using NovaWallet.Application.Interfaces;
using NovaWallet.Presentation.DTOs;
using NovaWallet.Presentation.Filters;
using NovaWallet.Presentation.Middleware;
using Swashbuckle.AspNetCore.Annotations;

namespace NovaWallet.Presentation.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize]
[Produces("application/json")]
public sealed class WalletsController : ControllerBase
{
    private readonly IWalletService _walletService;

    public WalletsController(IWalletService walletService) => _walletService = walletService;

    [HttpPost("wallets")]
    [SwaggerOperation(Summary = "Create a wallet for a customer.")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateWallet(
        [FromBody] CreateWalletRequest request,
        CancellationToken ct)
    {
        var wallet = await _walletService.CreateWalletAsync(request, ct);
        return CreatedAtAction(nameof(GetBalance), new { walletId = wallet.Id }, wallet);
    }

    [HttpGet("wallets/{walletId:guid}/balance")]
    [SwaggerOperation(Summary = "Get wallet balance.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBalance(Guid walletId, CancellationToken ct)
    {
        var balance = await _walletService.GetBalanceAsync(walletId, GetSubject(), ct);
        return Ok(balance);
    }

    [HttpPost("wallets/{walletId:guid}/credit")]
    [SwaggerOperation(Summary = "Credit a wallet (simulates an inbound NIP transfer).")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreditWallet(
        Guid walletId,
        [FromBody] CreditWalletRequest request,
        CancellationToken ct)
    {
        var tx = await _walletService.CreditWalletAsync(walletId, request, GetSubject(), ct);
        return Ok(tx);
    }

    [HttpPost("transfers")]
    [EnableRateLimiting("TransferPolicy")]
    [RequireIdempotencyKey]
    [SwaggerOperation(Summary = "Transfer funds between wallets. Idempotency-Key header required.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Transfer(
        [FromBody] TransferRequest request,
        [FromHeader(Name = RequireIdempotencyKeyAttribute.HeaderName)] string idempotencyKey,
        CancellationToken ct)
    {
        var correlationId = HttpContext.Items[CorrelationIdMiddleware.HeaderName]?.ToString() ?? string.Empty;
        var result = await _walletService.TransferAsync(request, idempotencyKey, GetSubject(), correlationId, ct);
        return Ok(result);
    }

    [HttpGet("wallets/{walletId:guid}/statement")]
    [SwaggerOperation(Summary = "Paginated transaction history, newest first.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatement(
        Guid walletId,
        [FromQuery] StatementQuery query,
        CancellationToken ct)
    {
        var statement = await _walletService.GetStatementAsync(walletId, GetSubject(), query.Page, query.PageSize, ct);
        return Ok(statement);
    }

    private string GetSubject() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("sub")?.Value
        ?? throw new UnauthorizedAccessException("JWT subject claim is missing.");
}
