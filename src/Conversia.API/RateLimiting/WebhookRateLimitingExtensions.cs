using System.Threading.RateLimiting;
using Conversia.API.Configuration;

namespace Conversia.API.RateLimiting;

public static class WebhookRateLimitingExtensions
{
    public const string PolicyName = "webhook";

    /// <summary>
    /// A policy of its own: a flood of webhook calls must not spend the login budget, nor the other way round. It is a generous fixed
    /// window per client address (the provider, not people, calls it); it runs before authentication, so a throttled call costs nothing.
    /// </summary>
    public static IServiceCollection AddWebhookRateLimiting(this IServiceCollection services, WebhookEndpointOptions options)
        => services.AddRateLimiter(limiter => limiter.AddPolicy(PolicyName, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            ClientPartition.Of(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.RateLimitPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            })));
}
