using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Conversia.Application.Metrics;

namespace Conversia.API.RateLimiting;

public static class HeavyQueryRateLimitingExtensions
{
    public const string PolicyName = "heavy-queries";

    /// <summary>
    /// One budget per authenticated user for the endpoints that aggregate or page through the whole conversation table
    /// (<c>/api/metrics/*</c> and <c>GET /api/conversations</c>): a dashboard that polls, or a script that loops, must not be able to
    /// keep the database busy. Fixed window of one minute, <c>Metrics:RateLimitPerMinute</c> requests. Users are told apart by the
    /// <c>sub</c> claim, so two people behind one office address do not share a budget; a request with no user (it will be a 401) falls
    /// back to the client address. The limiter runs after authentication for that reason (see <c>Program.cs</c>).
    /// </summary>
    public static IServiceCollection AddHeavyQueryRateLimiting(this IServiceCollection services, MetricsOptions options)
        => services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = TooManyRequestsResponse.WriteAsync;
            limiter.AddPolicy(PolicyName, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                PartitionOf(httpContext),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.RateLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

    private static string PartitionOf(HttpContext httpContext)
    {
        var user = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return string.IsNullOrEmpty(user) ? "ip:" + ClientPartition.Of(httpContext) : "user:" + user;
    }
}
