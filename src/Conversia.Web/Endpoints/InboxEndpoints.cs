using Conversia.Web.Authentication;
using Conversia.Web.Components.Inbox;
using Conversia.Web.Http;
using Conversia.Web.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Conversia.Web.Endpoints;

/// <summary>The <c>Inbox</c> settings of the Web: how often the inbox and an open conversation poll (docs/MESSAGING.md, "Inbox").</summary>
public sealed class InboxOptions
{
    public const string SectionName = "Inbox";

    /// <summary>Seconds between two polls; 2 to 300, 5 by default.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Requests per minute a signed-in user may make to each polled endpoint of the Web; 60 by default.</summary>
    public int PollRateLimitPerMinute { get; set; } = 60;
}

/// <summary>
/// What the page scripts poll (docs/MESSAGING.md, "Inbox"): the inbox rows as HTML (with an <c>ETag</c>, so an unchanged inbox is a
/// bodiless <c>304</c>), the version of an open conversation, and "the advisor is typing". All need the session; the API decides the rest.
/// </summary>
public static class InboxEndpoints
{
    public const string PollingPolicy = "web-polling";

    public static IServiceCollection AddInboxPolling(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new InboxOptions();
        configuration.GetSection(InboxOptions.SectionName).Bind(options);
        if (options.PollIntervalSeconds is < 2 or > 300)
        {
            throw new InvalidOperationException("Inbox:PollIntervalSeconds must be between 2 and 300.");
        }

        if (options.PollRateLimitPerMinute is < 1 or > 100000)
        {
            throw new InvalidOperationException("Inbox:PollRateLimitPerMinute must be between 1 and 100000.");
        }

        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));

        // What the page scripts call on their own, per signed-in user and endpoint (a tab left open, many tabs, a loop): Inbox:PollRateLimitPerMinute.
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(PollingPolicy, httpContext => System.Threading.RateLimiting.RateLimitPartition.GetSlidingWindowLimiter(
                $"{httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? httpContext.Connection.RemoteIpAddress?.ToString()}:{httpContext.GetEndpoint()?.DisplayName}",
                _ => new System.Threading.RateLimiting.SlidingWindowRateLimiterOptions
                {
                    PermitLimit = options.PollRateLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                }));
        });
        return services;
    }

    public static WebApplication MapInboxEndpoints(this WebApplication app)
    {
        app.MapGet("/bandeja/lista", ListAsync).RequireAuthorization().RequireRateLimiting(PollingPolicy);
        app.MapGet("/conversaciones/{id:guid}/version", VersionAsync).RequireAuthorization().RequireRateLimiting(PollingPolicy);
        app.MapPost("/conversaciones/{id:guid}/escribiendo", TypingAsync).RequireAuthorization().RequireValidAntiforgery().RequireRateLimiting(PollingPolicy);
        app.MapPost("/conversaciones/{id:guid}/leido", ReadAsync).RequireAuthorization().RequireValidAntiforgery().RequireRateLimiting(PollingPolicy);
        return app;
    }

    private static async Task<IResult> ListAsync(
        HttpContext context, IApiClient api, TimeProvider clock, string? filtro, string? sucursal, CancellationToken cancellationToken)
    {
        var token = await context.GetApiTokenAsync();
        if (token is null)
        {
            return Results.Unauthorized();
        }

        // The browser's If-None-Match goes to the API as is: the tag is the API's inbox version, so an unchanged inbox costs the API one cheap
        // query and this page nothing to render.
        var page = await api.GetInboxPageAsync(token, InboxQueryString.FromPage(filtro, sucursal), context.Request.Headers.IfNoneMatch.ToString(), cancellationToken);
        if (page.ETag is { } tag)
        {
            context.Response.Headers.ETag = tag;
        }

        if (page.NotModified)
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        if (!page.Result.IsSuccess)
        {
            return StatusOf(page.Result.Status);
        }

        return new RazorComponentResult<InboxList>(new { Response = page.Result.Value!, NowUtc = clock.GetUtcNow().UtcDateTime });
    }

    private static async Task<IResult> VersionAsync(HttpContext context, IApiClient api, Guid id, CancellationToken cancellationToken)
    {
        var token = await context.GetApiTokenAsync();
        if (token is null)
        {
            return Results.Unauthorized();
        }

        var result = await api.GetConversationVersionAsync(token, id, cancellationToken);
        return result.IsSuccess
            ? Results.Json(new { version = ConversationVersion.Of(result.Value!.MessageCount, result.Value.Status, result.Value.WindowOpen) })
            : StatusOf(result.Status);
    }

    // The read receipt is posted by the page's script once it is drawn and visible (docs/MESSAGING.md, "Read receipts and typing"): never by
    // a page render, so a prefetch, a reload or a page without JavaScript sends nothing.
    private static async Task<IResult> ReadAsync(HttpContext context, IApiClient api, Guid id, CancellationToken cancellationToken)
    {
        var token = await context.GetApiTokenAsync();
        if (token is null)
        {
            return Results.Unauthorized();
        }

        var result = await api.MarkConversationReadAsync(token, id, cancellationToken);
        return result.IsSuccess ? Results.NoContent() : StatusOf(result.Status);
    }

    // Never an error page: a typing indicator that could not be sent changes nothing for the advisor (the API logged why).
    private static async Task<IResult> TypingAsync(HttpContext context, IApiClient api, Guid id, CancellationToken cancellationToken)
    {
        var token = await context.GetApiTokenAsync();
        if (token is null)
        {
            return Results.Unauthorized();
        }

        var result = await api.SendTypingAsync(token, id, cancellationToken);
        return result.IsSuccess ? Results.NoContent() : StatusOf(result.Status);
    }

    private static IResult StatusOf(ApiResultStatus status) => status switch
    {
        ApiResultStatus.Unauthorized => Results.Unauthorized(),
        ApiResultStatus.Forbidden or ApiResultStatus.TenantNotActive => Results.StatusCode(StatusCodes.Status403Forbidden),
        ApiResultStatus.NotFound => Results.NotFound(),
        ApiResultStatus.ValidationFailed => Results.BadRequest(),
        _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
    };
}
