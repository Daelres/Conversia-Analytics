using System.Threading.RateLimiting;
using Conversia.Infrastructure.Configuration;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.RateLimiting;

public static class ErrorReportRateLimitingExtensions
{
    public const string PolicyName = "error-report";

    /// <summary>A policy of its own: a flood of reports must not spend the login budget, nor the other way round.</summary>
    public static IServiceCollection AddErrorReportRateLimiting(this IServiceCollection services, ErrorLoggingOptions options)
        => services.AddRateLimiter(limiter => limiter.AddPolicy(PolicyName, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            ClientPartition.Of(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.ReportRateLimitPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            })));
}
