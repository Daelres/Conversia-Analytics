using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Conversia.Application.Reports;

namespace Conversia.API.RateLimiting;

/// <summary>
/// The limits of the manager reports (docs/REPORTS.md): the preview writes a report on demand (it may call the model), so each user has
/// <c>Reports:PreviewsPerHour</c> per hour; the anonymous unsubscribe endpoint has a budget per client address against guessing.
/// </summary>
public static class ReportRateLimitingExtensions
{
    public const string PreviewPolicy = "report-preview";
    public const string UnsubscribePolicy = "report-unsubscribe";

    public static IServiceCollection AddReportRateLimiting(this IServiceCollection services, ReportSettings settings)
        => services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = TooManyRequestsResponse.WriteAsync;
            limiter.AddPolicy(PreviewPolicy, httpContext =>
            {
                var user = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                return RateLimitPartition.GetFixedWindowLimiter(
                    string.IsNullOrEmpty(user) ? "ip:" + ClientPartition.Of(httpContext) : "user:" + user,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.PreviewsPerHour, Window = TimeSpan.FromHours(1), QueueLimit = 0 });
            });
            limiter.AddPolicy(UnsubscribePolicy, httpContext =>
            {
                // The signature is checked here (cheap, no database): a valid link gets a budget of its own, every invalid token shares one per client.
                var token = httpContext.Request.RouteValues["token"] as string;
                var key = httpContext.RequestServices.GetRequiredService<ReportUnsubscribeKey>().Key;
                return ReportUnsubscribeToken.TryRead(token, key, out var recipient)
                    ? RateLimitPartition.GetFixedWindowLimiter("link:" + recipient.ToString("N"),
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.UnsubscribePerTokenPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetFixedWindowLimiter("invalid:" + ClientPartition.Of(httpContext),
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.UnsubscribeJunkPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
        });
}
