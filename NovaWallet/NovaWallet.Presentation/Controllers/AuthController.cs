using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.Presentation.DTOs;
using NovaWallet.Presentation.Services;
using Swashbuckle.AspNetCore.Annotations;

namespace NovaWallet.Presentation.Controllers;

[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
public sealed class AuthController : ControllerBase
{
    private readonly IMockTokenService _tokenService;

    public AuthController(IMockTokenService tokenService) => _tokenService = tokenService;

    [HttpPost("token")]
    [SwaggerOperation(Summary = "Issue a mock JWT for development/testing. Not for production use.")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult IssueToken([FromBody] TokenRequest request)
    {
        return Ok(_tokenService.Issue(request.CustomerId!));
    }
}
