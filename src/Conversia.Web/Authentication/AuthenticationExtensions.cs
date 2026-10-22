using System.Security.Claims;
using Conversia.Web.Http;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Conversia.Web.Authentication;

public static class AuthenticationExtensions
{
    public const string LoginPath = "/login";

    /// <summary>The platform administrator's own cookie scheme: another cookie, another data-protection purpose, another login. See <see cref="AdminSessionCookieExtensions"/>.</summary>
    public const string AdminScheme = "ConversiaAdmin";

    public const string AdminLoginPath = "/admin/login";

    /// <summary>The policy of every administrator page and form: signed in under <see cref="AdminScheme"/> and nothing else.</summary>
    public const string AdminPolicy = "PlatformAdmin";

    /// <summary>The role claim the Web puts on an administrator's cookie (the API alone decides what the token may do).</summary>
    public const string AdminRole = "platform_admin";

    public static IServiceCollection AddWebAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = LoginPath;
                options.ReturnUrlParameter = "returnUrl";
                options.Cookie.Name = ".Conversia.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                // Secure whenever the request is https; "Always" would break the http-only local profile.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                // Sliding renewal would let the cookie outlive the JWT it carries.
                options.SlidingExpiration = false;
            })
            // The administrator never shares a session with a company user: a second scheme with its own cookie. A company cookie is not
            // read here and this one is not read by the company's pages, so neither kind of session can open the other's pages.
            .AddCookie(AdminScheme, options =>
            {
                options.LoginPath = AdminLoginPath;
                options.AccessDeniedPath = AdminLoginPath;
                options.ReturnUrlParameter = "returnUrl";
                options.Cookie.Name = ".Conversia.Admin";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.SlidingExpiration = false;
            });
        services.AddAuthorization(options => options.AddPolicy(AdminPolicy, policy => policy
            .AddAuthenticationSchemes(AdminScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(ClaimTypes.Role, AdminRole)));
        services.AddCascadingAuthenticationState();
        return services;
    }

    public static WebApplication MapLogout(this WebApplication app)
    {
        app.MapPost("/admin/logout", async (HttpContext context) =>
            {
                await context.SignOutPlatformAdminAsync();
                return Results.LocalRedirect(AdminLoginPath);
            })
            // The antiforgery token of an administrator page is bound to the administrator's identity, so the endpoint that checks it
            // must see the same one: it runs under the administrator policy like every other administrator request.
            .RequireAuthorization(AdminPolicy)
            .RequireValidAntiforgery();

        app.MapPost("/logout", async (HttpContext context) =>
            {
                await context.SignOutApiUserAsync();
                return Results.LocalRedirect("/");
            })
            .RequireValidAntiforgery();
        return app;
    }
}
