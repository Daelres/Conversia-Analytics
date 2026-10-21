using Conversia.API.RateLimiting;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.Controllers;

/// <summary>The platform administrator's own sign-in. Separate from the companies' login: other audience, other key, no tenant.</summary>
[ApiController]
[Route("api/admin/auth")]
[AllowAnonymous]
public class AdminAuthController : ControllerBase
{
    private readonly IPlatformAdminAuthService _auth;

    public AdminAuthController(IPlatformAdminAuthService auth)
    {
        _auth = auth;
    }

    /// <summary>Wrong email, wrong password and a deactivated administrator all answer the same 401.</summary>
    [HttpPost("login")]
    [EnableRateLimiting(AdminLoginRateLimitingExtensions.PolicyName)]
    public async Task<ActionResult<AdminAuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var response = await _auth.LoginAsync(request, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(response);
    }
}
