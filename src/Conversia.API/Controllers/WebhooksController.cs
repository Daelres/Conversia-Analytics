using Conversia.API.Configuration;
using Conversia.API.Localization;
using Conversia.API.RateLimiting;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.Exceptions;
using Conversia.Application.Ingestion;
using Conversia.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

/// <summary>
/// The anonymous webhook the provider calls (no JWT: the per-channel secret authenticates it, in <c>X-Webhook-Secret</c> or as the password of <c>Authorization: Basic</c>). It validates,
/// stores the delivery and answers 200 at once; every bit of business work happens later in the background worker.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route(WebhookRoutes.Dialog360Base)]
public class WebhooksController : ControllerBase
{
    private readonly IWebhookIngestionService _ingestion;
    private readonly WebhookEndpointOptions _options;
    private readonly WebhookRateLimiter _rateLimiter;

    public WebhooksController(IWebhookIngestionService ingestion, WebhookEndpointOptions options, WebhookRateLimiter rateLimiter)
    {
        _ingestion = ingestion;
        _options = options;
        _rateLimiter = rateLimiter;
    }

    [HttpPost("{channelKey}")]
    public async Task<IActionResult> Receive360Dialog(string channelKey, CancellationToken cancellationToken)
    {
        var clientIp = ClientPartition.Of(HttpContext);

        // An address that already failed too often is refused before anything else is spent on it.
        if (_rateLimiter.RetryAfterIfBlocked(clientIp) is { } blockedFor)
        {
            return TooManyRequests(blockedFor);
        }

        // Authenticate before reading the body: a caller without the secret never gets us to read (or store) anything. An unknown
        // key, an inactive channel and a wrong secret are the same answer.
        var channel = await AuthenticateAsync(channelKey, cancellationToken);
        if (channel is null)
        {
            _rateLimiter.RegisterFailedAuthentication(clientIp);
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: ProblemTexts.WebhookUnauthorizedTitle,
                detail: ProblemTexts.WebhookUnauthorizedDetail);
        }

        if (_rateLimiter.RetryAfterIfChannelOverLimit(channel.ChannelId) is { } overLimitFor)
        {
            return TooManyRequests(overLimitFor);
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

    // Two ways to present the same channel secret. X-Webhook-Secret is tried first; only when it does not authenticate (absent or wrong) is
    // the Basic password tried. Either one failing alone is a failed authentication, and a request counts once whatever it presented. Nothing
    // is ever read from the URL or the query string.
    private async Task<WebhookChannelAccess?> AuthenticateAsync(string channelKey, CancellationToken cancellationToken)
    {
        var presented = new List<string>(2);
        var headerSecret = Request.Headers[WebhookRoutes.SecretHeader].ToString();
        if (headerSecret.Length > 0)
        {
            presented.Add(headerSecret);
        }

        if (WebhookBasicAuth.TryReadPassword(Request.Headers.Authorization.ToString()) is { } basicSecret)
        {
            presented.Add(basicSecret);
        }

        // Nothing usable presented: still one lookup, so a bare request costs what a wrong secret costs.
        if (presented.Count == 0)
        {
            return await _ingestion.AuthenticateAsync(channelKey, null, cancellationToken);
        }

        foreach (var secret in presented)
        {
            if (await _ingestion.AuthenticateAsync(channelKey, secret, cancellationToken) is { } access)
            {
                return access;
            }
        }

        return null;
    }

    private ObjectResult TooManyRequests(int retryAfterSeconds)
    {
        Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: ProblemTexts.TooManyRequestsTitle,
            detail: ProblemTexts.TooManyWebhooksDetail);
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
