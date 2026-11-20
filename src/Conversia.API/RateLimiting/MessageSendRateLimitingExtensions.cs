using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Configuration;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.RateLimiting;

public static class MessageSendRateLimitingExtensions
{
    public const string PolicyName = "message-sending";

    private const string TenantClaim = "tenant_id";

    /// <summary>The sliding windows move in steps of ten seconds.</summary>
    private const int SegmentsPerWindow = 6;

    /// <summary>
    /// Two budgets for <c>POST /api/conversations/{id}/messages</c>, both sliding windows of one minute (a fixed window would let a
    /// burst at the end of one minute be followed by another at the start of the next), both counted whatever the outcome:
    /// <list type="bullet">
    /// <item><b>Per user</b> (<c>Messaging:SendRateLimitPerMinute</c>), the endpoint's policy: a loop or a stuck client of one person
    /// cannot flood a customer. Users are told apart by the <c>sub</c> claim, as in <see cref="HeavyQueryRateLimitingExtensions"/>.</item>
    /// <item><b>Per company</b> (<c>Messaging:TenantSendRateLimitPerMinute</c>, or the company's own limit set by the platform administrator,
    /// <see cref="ITenantSendRateLimits"/>), keyed by the <c>tenant_id</c> claim: many users, or
    /// many accounts, of one company cannot together spend the provider's quota or crowd the others out. It is the application's global
    /// limiter, and applies only to endpoints that carry this policy; every other request passes through it untouched.</item>
    /// </list>
    /// The company budget is checked first, so a request it refuses does not spend the user's.
    /// </summary>
    public static IServiceCollection AddMessageSendRateLimiting(this IServiceCollection services, MessageSendingOptions options)
        => services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = TooManyRequestsResponse.WriteAsync;
            limiter.AddPolicy(PolicyName, httpContext => Window(UserPartitionOf(httpContext), options.SendRateLimitPerMinute));
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                if (!IsSend(httpContext) || TenantOf(httpContext) is not { } tenant)
                {
                    return RateLimitPartition.GetNoLimiter(string.Empty);
                }

                // The company's own limit when the administrator set one (docs/PLATFORM_ADMIN.md, "Limits"), else the deployment's. The
                // limit is part of the partition, so a new limit starts a fresh window instead of keeping the old one.
                var limit = (Guid.TryParse(tenant, out var id) ? httpContext.RequestServices.GetService<ITenantSendRateLimits>()?.OverrideFor(id) : null)
                    ?? options.TenantSendRateLimitPerMinute;
                return Window($"tenant:{tenant}:{limit}", limit);
            });
        });

    private static RateLimitPartition<string> Window(string partition, int permitLimit)
        => RateLimitPartition.GetSlidingWindowLimiter(partition, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = SegmentsPerWindow,
            QueueLimit = 0,
        });

    private static bool IsSend(HttpContext httpContext)
        => httpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == PolicyName;

    // No tenant claim means no valid token: the request will be a 401 and has no company to charge.
    private static string? TenantOf(HttpContext httpContext)
    {
        var tenant = httpContext.User.FindFirst(TenantClaim)?.Value;
        return string.IsNullOrEmpty(tenant) ? null : tenant;
    }

    private static string UserPartitionOf(HttpContext httpContext)
    {
        var user = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return string.IsNullOrEmpty(user) ? "ip:" + ClientPartition.Of(httpContext) : "user:" + user;
    }
}
