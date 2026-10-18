using System.Globalization;
using System.Threading.RateLimiting;
using Conversia.API.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.RateLimiting;

/// <summary>The one 429 answer of every rate-limit policy: Spanish problem+json plus <c>Retry-After</c>, worded for the policy that refused.</summary>
internal static class TooManyRequestsResponse
{
    private const string ProblemJsonContentType = "application/problem+json";

    private static readonly TimeSpan FallbackRetryAfter = TimeSpan.FromSeconds(60);

    public static async ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.RetryAfter = RetryAfterSeconds(context.Lease).ToString(CultureInfo.InvariantCulture);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = ProblemTexts.TooManyRequestsTitle,
            Detail = DetailFor(context.HttpContext),
        };

        // WriteAsJsonAsync overwrites a pre-set ContentType, so the problem media type is passed to it.
        await response.WriteAsJsonAsync(problem, options: null, ProblemJsonContentType, cancellationToken);
    }

    private static string DetailFor(HttpContext httpContext)
    {
        var policy = httpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        return policy switch
        {
            ErrorReportRateLimitingExtensions.PolicyName => ProblemTexts.TooManyReportsDetail,
            WebhookRateLimitingExtensions.PolicyName => ProblemTexts.TooManyWebhooksDetail,
            _ => ProblemTexts.TooManyLoginsDetail,
        };
    }

    private static int RetryAfterSeconds(RateLimitLease lease)
    {
        var wait = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : FallbackRetryAfter;
        return Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
    }
}
