using Conversia.Application.DTOs.Sandbox;
using Conversia.Web.Authentication;
using Conversia.Web.Http;
using Conversia.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Conversia.Web.Endpoints;

/// <summary>
/// The media of a conversation in the Web (docs/MESSAGING.md, "Media"): the pass-through of a stored file (the browser never talks to the API
/// or to the store, and the session's token never reaches the page) and the two attachment forms of the conversation page. Every outcome of a
/// form is a redirect back to the conversation with a fixed notice code.
/// </summary>
public static class MediaEndpoints
{
    /// <summary>The largest attachment form: Meta's document limit and room for the form around it (the API checks the limits per kind).</summary>
    public const long MaxUploadBodyBytes = 100L * 1024 * 1024 + 64 * 1024;

    public static WebApplication MapMediaEndpoints(this WebApplication app)
    {
        app.MapGet("/conversaciones/{id:guid}/adjuntos/{messageId:guid}", FileAsync).RequireAuthorization();
        app.MapPost("/conversaciones/{id:guid}/adjuntar", SendAsync).RequireAuthorization().RequireValidAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBodyBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxUploadBodyBytes });
        app.MapPost("/conversaciones/{id:guid}/adjunto-de-prueba", SandboxAsync).RequireAuthorization().RequireValidAntiforgery();
        return app;
    }

    // The headers worth passing on; anything else of the API's answer stays there.
    private static readonly string[] PassedContentHeaders = [HeaderNames.ContentType, HeaderNames.ContentLength, HeaderNames.ContentRange, HeaderNames.ContentDisposition];

    private static async Task FileAsync(HttpContext context, IApiClient api, Guid id, Guid messageId, CancellationToken cancellationToken)
    {
        var token = await context.GetApiTokenAsync();
        if (token is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var media = await api.OpenMessageMediaAsync(token, id, messageId, context.Request.Headers.Range.ToString(), cancellationToken);
        if (media.Response is not { } response || media.Status is not (StatusCodes.Status200OK or StatusCodes.Status206PartialContent or StatusCodes.Status416RangeNotSatisfiable))
        {
            context.Response.StatusCode = media.Status switch
            {
                StatusCodes.Status401Unauthorized => StatusCodes.Status401Unauthorized,
                StatusCodes.Status403Forbidden => StatusCodes.Status403Forbidden,
                StatusCodes.Status404NotFound => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status503ServiceUnavailable,
            };
            return;
        }

        context.Response.StatusCode = media.Status;
        foreach (var name in PassedContentHeaders)
        {
            if (response.Content.Headers.TryGetValues(name, out var values))
            {
                context.Response.Headers[name] = values.ToArray();
            }
        }

        context.Response.Headers[HeaderNames.AcceptRanges] = "bytes";
        // The same rules as the API: the type is the checked one and never sniffed, nothing is cached outside the browser, and the file can
        // never run script in the site's origin (docs/MESSAGING.md, "Serving media").
        context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        
        context.Response.Headers[HeaderNames.CacheControl] = "private, max-age=300";
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await body.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private static async Task<IResult> SendAsync(HttpContext context, IApiClient api, Guid id, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return Back(id, "vacio");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return Back(id, "vacio");
        }

        var token = await context.GetApiTokenAsync();
        if (token is null)
        {
            return await EndSessionAsync(context);
        }

        await using var content = file.OpenReadStream();
        var caption = form["caption"].ToString();
        var result = await api.SendMediaMessageAsync(token, id, content, file.ContentType, string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(), cancellationToken);
        return result.Status switch
        {
            ApiResultStatus.Success => Back(id, "enviado"),
            ApiResultStatus.Unauthorized => await EndSessionAsync(context),
            ApiResultStatus.TenantNotActive => Results.LocalRedirect(LocalReturnUrl.PendingAccount),
            _ when result.ProblemCode is { } code && SendFailure.Describe(code) is not null => Back(id, code),
            _ => Back(id, "desconocido"),
        };
    }

    private static async Task<IResult> SandboxAsync(HttpContext context, IApiClient api, Guid id, CancellationToken cancellationToken)
    {
        var form = context.Request.HasFormContentType ? await context.Request.ReadFormAsync(cancellationToken) : null;
        var kind = form?["attachment"].ToString() is "document" ? "document" : "image";
        var token = await context.GetApiTokenAsync();
        if (token is null)
        {
            return await EndSessionAsync(context);
        }

        var result = await api.SendSandboxCustomerMessageAsync(token, id, new SandboxCustomerMessageRequest { Attachment = kind }, cancellationToken);
        return result.Status == ApiResultStatus.Unauthorized ? await EndSessionAsync(context) : Back(id, result.IsSuccess ? "simulado" : "desconocido");
    }

    private static IResult Back(Guid id, string notice) => Results.LocalRedirect($"/conversaciones/{id}?adjunto={Uri.EscapeDataString(notice)}#adjuntar");

    private static async Task<IResult> EndSessionAsync(HttpContext context)
    {
        await context.SignOutApiUserAsync();
        return Results.LocalRedirect(AuthenticationExtensions.LoginPath);
    }
}
