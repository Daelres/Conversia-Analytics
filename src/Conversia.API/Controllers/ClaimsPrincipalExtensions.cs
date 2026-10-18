using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Conversia.API.Controllers;

internal static class ClaimsPrincipalExtensions
{
    /// <summary>The tenant of the authenticated caller, from the <c>tenant_id</c> claim of the JWT (never from the request).</summary>
    public static Guid GetTenantId(this ClaimsPrincipal user)
        => Guid.Parse(user.FindFirst("tenant_id")!.Value);

    /// <summary>The authenticated user, from the <c>sub</c> claim of the JWT; null when the claim is missing or is not an id.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var id) ? id : null;
}
