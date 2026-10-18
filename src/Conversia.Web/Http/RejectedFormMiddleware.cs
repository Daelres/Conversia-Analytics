using Microsoft.AspNetCore.Antiforgery;

namespace Conversia.Web.Http;

public static class RejectedFormMiddleware
{
    /// <summary>
    /// A Blazor form post with a bad antiforgery token is answered with a bare 400 that still says <c>text/html</c>, which keeps the
    /// status-code pages from re-running it. Dropping the content type of that empty answer lets them render <c>/status/400</c> (the "invalid
    /// or expired request" page). The minimal endpoints (logout, permissions) answer with a body of their own and are left alone.
    /// </summary>
    public static IApplicationBuilder UseRejectedFormPage(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            await next();

            var response = context.Response;
            if (response.StatusCode == StatusCodes.Status400BadRequest
                && !response.HasStarted
                && context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false }
                && response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
            {
                response.ContentType = null;

                // The page is re-run as a plain read: the rejected form body must not be touched again.
                context.Request.Method = HttpMethods.Get;
                context.Request.ContentType = null;
            }
        });
}
