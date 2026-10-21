using System.Threading.RateLimiting;
using Conversia.API.Configuration;

namespace Conversia.API.RateLimiting;

/// <summary>
/// The platform administrator's login gets the limit of the companies' login (<c>RateLimiting:Login</c>) but a budget of its own per
/// client address, so a flood against one cannot lock the other out.
/// </summary>
public static class AdminLoginRateLimitingExtensions
{
    public const string PolicyName = "admin-login";

    public static IServiceCollection AddAdminLoginRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var login = LoginRateLimitOptionsValidator.GetValidatedOptions(configuration);

        return services.AddRateLimiter(options => options.AddPolicy(PolicyName, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            ClientPartition.Of(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = login.PermitLimit,
                Window = login.Window,
                QueueLimit = 0,
            })));
    }
}
