using Conversia.API.Configuration;
using Conversia.API.Localization;
using Conversia.API.RateLimiting;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.Controllers;

/// <summary>
/// The anonymous webhook the provider calls (no JWT: the per-channel secret in <c>X-Webhook-Secret</c> authenticates it). It validates,
/// stores the delivery and answers 200 at once; every bit of business work happens later in the background worker.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route(WebhookRoutes.Dialog360Base)]
public class WebhooksController : ControllerBase
{
    private readonly IWebhookIngestionService _ingestion;
    private readonly WebhookEndpointOptions _options;

    public WebhooksController(IWebhookIngestionService ingestion, WebhookEndpointOptions options)
    {
        _ingestion = ingestion;
        _options = options;
    }

    [HttpPost("{channelKey}")]
    [EnableRateLimiting(WebhookRateLimitingExtensions.PolicyName)]
    public async Task<IActionResult> Receive360Dialog(string channelKey, CancellationToken cancellationToken)
    {
        var secret = Request.Headers[WebhookRoutes.SecretHeader].ToString();

        // Authenticate before reading the body: a caller without the secret never gets us to read (or store) anything. An unknown
        // key, an inactive channel and a wrong secret are the same answer.
        var channel = await _ingestion.AuthenticateAsync(channelKey, secret, cancellationToken);
        if (channel is null)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: ProblemTexts.WebhookUnauthorizedTitle,
                detail: ProblemTexts.WebhookUnauthorizedDetail);
        }

        var body = await ReadBodyAsync(cancellationToken);
        if (body is null)
        {
            return Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: ProblemTexts.PayloadTooLargeTitle,
                detail: ProblemTexts.PayloadTooLargeDetail);
        }

        return await _ingestion.StoreAsync(channel, body, cancellationToken) switch
        {
            // Stored or already stored: the provider must see 200 either way, or it keeps retrying.
            WebhookStoreResult.Stored or WebhookStoreResult.Duplicate => (IActionResult)Ok(),
            _ => Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: ProblemTexts.InvalidRequestTitle,
                detail: InvalidWebhookPayloadException.DefaultMessage),
        };
    }

    // The whole body, or null when it is over the limit. Reading stops at the limit, so an oversized (or endless) body costs no more than that.
    private async Task<byte[]?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        var limit = _options.MaxBodyBytes;
        if (Request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
