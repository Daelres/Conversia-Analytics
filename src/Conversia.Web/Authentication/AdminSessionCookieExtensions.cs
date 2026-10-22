using System.Security.Claims;
using Conversia.Application.DTOs.Admin;
using Microsoft.AspNetCore.Authentication;

namespace Conversia.Web.Authentication;

/// <summary>
/// The platform administrator's session, the twin of <see cref="SessionCookieExtensions"/> for the company's: the administrator API's token lives
/// only inside its own encrypted cookie (scheme <see cref="AuthenticationExtensions.AdminScheme"/>) and is read back per request. The cookie
/// lasts as long as the token (no sliding renewal) and is a browser-session cookie, so closing the browser ends it.
/// </summary>
public static class AdminSessionCookieExtensions
{
    private const string AccessTokenName = "access_token";

    /// <summary>False when the API's token carries no usable expiry; the caller then treats the API answer as malformed.</summary>
    public static async Task<bool> SignInPlatformAdminAsync(this HttpContext context, AdminAuthResponse auth)
    {
        var now = DateTimeOffset.UtcNow;
        if (!JwtExpiry.TryRead(auth.Token, out var expiresAt) || expiresAt <= now)
        {
            return false;
        }

        // No tenant claim: an administrator belongs to no company.
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, auth.AdminId.ToString()), new Claim(ClaimTypes.Role, AuthenticationExtensions.AdminRole)],
            AuthenticationExtensions.AdminScheme);
        var properties = new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = false,
            IssuedUtc = now,
            ExpiresUtc = expiresAt
        };
        properties.StoreTokens([new AuthenticationToken { Name = AccessTokenName, Value = auth.Token }]);

        await context.SignInAsync(AuthenticationExtensions.AdminScheme, new ClaimsPrincipal(identity), properties);
        return true;
    }

    public static Task<string?> GetPlatformAdminTokenAsync(this HttpContext context)
        => context.GetTokenAsync(AuthenticationExtensions.AdminScheme, AccessTokenName);

    public static Task SignOutPlatformAdminAsync(this HttpContext context)
        => context.SignOutAsync(AuthenticationExtensions.AdminScheme);
}
