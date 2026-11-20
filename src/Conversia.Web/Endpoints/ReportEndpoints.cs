using Conversia.Web.Services;

namespace Conversia.Web.Endpoints;

/// <summary>
/// The RFC 8058 one-click unsubscribe of the report emails (docs/REPORTS.md, "Unsubscribe"): the mail program posts
/// <c>List-Unsubscribe=One-Click</c> to the address of the <c>List-Unsubscribe</c> header, without a session or an antiforgery token (the
/// signed token of the link is the proof). A person who opens the link in a browser lands on <c>/informes/baja</c> and confirms with a button.
/// </summary>
public static class ReportEndpoints
{
    /// <summary>The unsubscribe landing and its one-click endpoint, per client address (the Web sees the real one): 30 a minute.</summary>
    public const string UnsubscribePolicy = "web-report-unsubscribe";

    public const int UnsubscribePerMinute = 30;

    public static IServiceCollection AddReportUnsubscribeRateLimiting(this IServiceCollection services)
        => services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(UnsubscribePolicy, httpContext => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-client",
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = UnsubscribePerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

    public static WebApplication MapReportEndpoints(this WebApplication app)
    {
        app.MapPost("/informes/baja/un-clic", UnsubscribeAsync).DisableAntiforgery().AllowAnonymous().RequireRateLimiting(UnsubscribePolicy);
        return app;
    }

    private static async Task<IResult> UnsubscribeAsync(HttpContext context, IApiClient api, CancellationToken cancellationToken)
    {
        var token = context.Request.Query["t"].ToString();
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return Plain(InvalidLink, StatusCodes.Status404NotFound);
        }

        var result = await api.UnsubscribeFromReportAsync(token, cancellationToken);
        return result.Status switch
        {
            ApiResultStatus.Success => Plain("Listo: ya no recibirás este informe por correo.", StatusCodes.Status200OK),
            ApiResultStatus.NotFound or ApiResultStatus.ValidationFailed => Plain(InvalidLink, StatusCodes.Status404NotFound),
            ApiResultStatus.RateLimited => Plain(UiMessages.TooManyAttempts(result.RetryAfterSeconds), StatusCodes.Status429TooManyRequests),
            _ => Plain(UiMessages.Unavailable, StatusCodes.Status503ServiceUnavailable),
        };
    }

    private const string InvalidLink = "El enlace no es válido o el informe ya no existe.";

    // Always with a body: an empty error answer would be re-run as a page by the status code pages.
    private static IResult Plain(string text, int status) => Results.Text(text, "text/plain; charset=utf-8", statusCode: status);
}
