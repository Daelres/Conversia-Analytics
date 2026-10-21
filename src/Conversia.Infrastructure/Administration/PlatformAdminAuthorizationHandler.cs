using System.IdentityModel.Tokens.Jwt;
using Conversia.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Conversia.Infrastructure.Administration;

public sealed class PlatformAdminRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// The caller carries a valid platform administrator token (the scheme of the policy already checked its signature, audience and expiry)
/// AND the administrator still exists and is active: deleting or deactivating one cuts its tokens off at once, not at expiry.
/// </summary>
public sealed class PlatformAdminAuthorizationHandler : AuthorizationHandler<PlatformAdminRequirement>
{
    private readonly IPlatformAdminRepository _admins;

    public PlatformAdminAuthorizationHandler(IPlatformAdminRepository admins)
    {
        _admins = admins;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformAdminRequirement requirement)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true
            || user.FindFirst(PlatformAdminTokens.RoleClaimType)?.Value != PlatformAdminTokens.RoleValue
            || user.FindFirst("tenant_id") is not null
            || !Guid.TryParse(user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var adminId))
        {
            return;
        }

        if (await _admins.IsActiveAsync(adminId))
        {
            context.Succeed(requirement);
        }
    }
}
