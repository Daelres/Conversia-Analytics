using Conversia.Web.Http;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Conversia.Web.Authentication;

public static class AuthenticationExtensions
{
    public const string LoginPath = "/login";

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
            });
        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        return services;
    }

    public static WebApplication MapLogout(this WebApplication app)
    {
        app.MapPost("/logout", async (HttpContext context) =>
            {
                await context.SignOutApiUserAsync();
                return Results.LocalRedirect("/");
            })
            .RequireValidAntiforgery();
        return app;
    }
}
