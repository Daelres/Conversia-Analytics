using System.Security.Claims;
using Conversia.Application.DTOs.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Conversia.Web.Authentication;

/// <summary>
/// The API's JWT lives only inside the encrypted auth cookie (as an authentication token) and is read
/// back per request; nothing is cached in a field, so concurrent users can never see each other's token.
/// </summary>
public static class SessionCookieExtensions
{
    private const string AccessTokenName = "access_token";

    /// <summary>The same claim name the API's token uses; it lets an error report say whose workspace was affected without a call to the API.</summary>
    public const string TenantIdClaimType = "tenant_id";

    /// <summary>False when the API's token carries no usable expiry; the caller then treats the API answer as malformed.</summary>
    public static async Task<bool> SignInApiUserAsync(this HttpContext context, AuthResponse auth)
    {
        var now = DateTimeOffset.UtcNow;
        if (!JwtExpiry.TryRead(auth.Token, out var expiresAt) || expiresAt <= now)
        {
            return false;
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, auth.UserId.ToString()), new Claim(TenantIdClaimType, auth.TenantId.ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = false,
            IssuedUtc = now,
            ExpiresUtc = expiresAt
        };
        properties.StoreTokens([new AuthenticationToken { Name = AccessTokenName, Value = auth.Token }]);

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), properties);
        return true;
    }

    public static Task<string?> GetApiTokenAsync(this HttpContext context)
        => context.GetTokenAsync(CookieAuthenticationDefaults.AuthenticationScheme, AccessTokenName);

    public static Task SignOutApiUserAsync(this HttpContext context)
        => context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
}
