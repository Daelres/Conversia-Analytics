using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Conversia.API.Configuration;

namespace Conversia.API.RateLimiting;

/// <summary>
/// Budgets per user for what pages call on their own (docs/MESSAGING.md, "Inbox" and "Read receipts and typing"), so a stuck script, many
/// open tabs or a loop cannot keep the database or the provider busy. Sliding windows of one minute; users are told apart by the <c>sub</c>
/// claim, a request without one (it will be a 401) by the client address.
/// <list type="bullet">
/// <item><see cref="PolicyName"/>: the polled reads (<c>GET /api/inbox</c>, <c>/version</c>, the template lists),
/// <c>Inbox:PollRateLimitPerMinute</c> (60) per user <b>and endpoint</b>, so the inbox and an open conversation do not share one budget.</item>
/// <item><see cref="PresencePolicyName"/>: read receipts and the typing indicator, <c>Messaging:PresenceRateLimitPerMinute</c> (30) per user;
/// each one is a request to the provider.</item>
/// </list>
/// </summary>
public static class PollingRateLimitingExtensions
{
    public const string PolicyName = "polling";
    public const string PresencePolicyName = "presence";
    public const string PollKey = "Inbox:PollRateLimitPerMinute";
    public const string PresenceKey = "Messaging:PresenceRateLimitPerMinute";

    public static IServiceCollection AddPollingRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var poll = ConfigurationReader.ReadInt(configuration, PollKey, 60, 1, 100000);
        var presence = ConfigurationReader.ReadInt(configuration, PresenceKey, 30, 1, 100000);
        return services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(PolicyName, httpContext => Window($"{UserOf(httpContext)}:{httpContext.GetEndpoint()?.DisplayName}", poll));
            limiter.AddPolicy(PresencePolicyName, httpContext => Window(UserOf(httpContext), presence));
        });
    }

    private static RateLimitPartition<string> Window(string partition, int permitLimit)
        => RateLimitPartition.GetSlidingWindowLimiter(partition, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
        });

    private static string UserOf(HttpContext httpContext)
    {
        var user = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return string.IsNullOrEmpty(user) ? "ip:" + ClientPartition.Of(httpContext) : "user:" + user;
    }
}
