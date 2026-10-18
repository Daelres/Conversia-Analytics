using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Conversia.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Conversia.Infrastructure.Authorization;

/// <summary>
/// Enforces permission-based policies — docs/USERS_TENANTS_PERMISSIONS.md,
/// "Authorization": (1) an owner's request always succeeds, (2) otherwise a
/// <c>UserPermissions</c> lookup scoped to the caller's own tenant (via the
/// repository's DbContext query filter) decides.
/// </summary>
public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private const string IsOwnerClaimType = "is_owner";

    private readonly IUserPermissionRepository _userPermissionRepository;

    public PermissionAuthorizationHandler(IUserPermissionRepository userPermissionRepository)
    {
        _userPermissionRepository = userPermissionRepository;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        // An unauthenticated/malformed-JWT principal must fail the
        // requirement without throwing: leaving it unmet lets ASP.NET Core's
        // authorization middleware challenge (401) rather than forbid (403)
        // or surface an unhandled 500.
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (IsOwner(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        var userId = GetUserId(context.User);
        if (userId is null)
        {
            return;
        }

        if (await _userPermissionRepository.HasPermissionAsync(userId.Value, requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }

    private static bool IsOwner(ClaimsPrincipal user)
        => bool.TryParse(user.FindFirst(IsOwnerClaimType)?.Value, out var isOwner) && isOwner;

    private static Guid? GetUserId(ClaimsPrincipal user)
    {
        var subClaim = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(subClaim, out var userId) ? userId : null;
    }
}
