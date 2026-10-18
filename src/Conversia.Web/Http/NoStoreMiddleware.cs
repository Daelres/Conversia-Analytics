namespace Conversia.Web.Http;

/// <summary>
/// Pages, redirects and error bodies can carry tenant data or sign-in state, so none may be cached.
/// Static assets (which have their own cache headers and non-HTML types) are left alone.
/// </summary>
public static class NoStoreMiddleware
{
    public static IApplicationBuilder UseNoStoreForDynamicResponses(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var contentType = context.Response.ContentType;
                if (string.IsNullOrEmpty(contentType) || contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers.CacheControl = "no-store";
                }

                return Task.CompletedTask;
            });

            await next();
        });
}
