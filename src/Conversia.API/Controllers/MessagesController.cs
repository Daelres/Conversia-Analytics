using Conversia.API.RateLimiting;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Messages;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.Controllers;

/// <summary>The messages the service has read, newest first, and a few counters to tell whether they are arriving.</summary>
[ApiController]
[Route("api/messages")]
[Authorize(Policy = nameof(Permission.ConversationsView))]
[EnableRateLimiting(HeavyQueryRateLimitingExtensions.PolicyName)]
public class MessagesController : ControllerBase
{
    private readonly IMessageFeedService _feed;

    public MessagesController(IMessageFeedService feed)
    {
        _feed = feed;
    }

    /// <summary>The newest messages of the company with their line and customer (name and last four digits of the number), filtered by line, direction and time.</summary>
    [HttpGet("recent")]
    public async Task<ActionResult<PagedResponse<RecentMessageItem>>> GetRecent([FromQuery] RecentMessagesQuery query, CancellationToken cancellationToken)
        => Ok(await _feed.RecentAsync(query, cancellationToken));

    /// <summary>Messages today, the last message in each direction, the last 24 hours and the last delivery that arrived on each line.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<MessagesSummaryResponse>> GetSummary(CancellationToken cancellationToken)
        => Ok(await _feed.SummaryAsync(cancellationToken));
}
