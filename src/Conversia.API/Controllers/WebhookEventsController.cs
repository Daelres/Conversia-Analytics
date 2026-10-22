using Conversia.API.RateLimiting;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Messages;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.Controllers;

/// <summary>The webhook deliveries the company's lines received, including the ones that stored nothing. Never their raw body.</summary>
[ApiController]
[Route("api/webhook-events")]
[Authorize(Policy = nameof(Permission.ConversationsView))]
[EnableRateLimiting(HeavyQueryRateLimitingExtensions.PolicyName)]
public class WebhookEventsController : ControllerBase
{
    private readonly IMessageFeedService _feed;

    public WebhookEventsController(IMessageFeedService feed)
    {
        _feed = feed;
    }

    /// <summary>Newest first, filtered by line and processing status. Each delivery carries its counts, an outcome in Spanish and its sanitized last error.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResponse<WebhookEventItem>>> GetEvents([FromQuery] WebhookEventsQuery query, CancellationToken cancellationToken)
        => Ok(await _feed.WebhookEventsAsync(query, cancellationToken));
}
