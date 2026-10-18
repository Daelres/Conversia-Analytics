using Conversia.API.RateLimiting;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("register-tenant")]
    public async Task<ActionResult<AuthResponse>> RegisterTenant(
        RegisterTenantRequest request,
        CancellationToken cancellationToken)
    {
        // Duplicate-email -> 409 and validation -> 400 are handled by
        // Role C's service logic / global exception middleware. This action
        // deliberately does not catch anything itself — see docs/WORKFLOW.md.
        var response = await _authService.RegisterTenantAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("login")]
    [EnableRateLimiting(LoginRateLimitingExtensions.PolicyName)]
    public async Task<ActionResult<AuthResponse>> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _authService.LoginAsync(request, cancellationToken);
        return Ok(response);
    }
}
