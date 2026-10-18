using System.Threading.RateLimiting;
using Conversia.API.Configuration;

namespace Conversia.API.RateLimiting;

public static class LoginRateLimitingExtensions
{
    public const string PolicyName = "login";

    public static IServiceCollection AddLoginRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var login = LoginRateLimitOptionsValidator.GetValidatedOptions(configuration);

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = TooManyRequestsResponse.WriteAsync;
            options.AddPolicy(PolicyName, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                ClientPartition.Of(httpContext),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = login.PermitLimit,
                    Window = login.Window,
                    QueueLimit = 0,
                }));
        });
    }
}
