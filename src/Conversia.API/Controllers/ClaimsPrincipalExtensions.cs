using System.Security.Claims;

namespace Conversia.API.Controllers;

internal static class ClaimsPrincipalExtensions
{
    /// <summary>The tenant of the authenticated caller, from the <c>tenant_id</c> claim of the JWT (never from the request).</summary>
    public static Guid GetTenantId(this ClaimsPrincipal user)
        => Guid.Parse(user.FindFirst("tenant_id")!.Value);
}
