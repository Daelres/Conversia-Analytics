using Conversia.API.RateLimiting;
using Conversia.Application.DTOs.Inbox;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace Conversia.API.Controllers;

/// <summary>
/// The inbox (docs/MESSAGING.md, "Inbox"): the open conversations to answer, by priority, for a page that polls every few seconds. The full
/// list carries an <c>ETag</c> (the inbox's version); a request whose <c>If-None-Match</c> is still current gets a bodiless <c>304</c> after
/// one cheap indexed query, without building the list. With <c>since</c>, only the rows that changed. Same permission and scope as the
/// conversation list; rate-limited per user like every polled endpoint.
/// </summary>
[ApiController]
[Route("api/inbox")]
[Authorize(Policy = nameof(Permission.ConversationsView))]
[EnableRateLimiting(PollingRateLimitingExtensions.PolicyName)]
public class InboxController(IInboxService inbox) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetInbox([FromQuery] InboxQuery query, CancellationToken cancellationToken)
    {
        string? known = null;
        if (EntityTagHeaderValue.TryParseList(Request.Headers.IfNoneMatch, out var tags) && tags.Count == 1)
        {
            known = tags[0].Tag.Value?.Trim('"');
        }

        var answer = await inbox.GetAsync(query, User.GetUserId(), known, cancellationToken);
        Response.Headers.CacheControl = "no-cache";
        if (query.Since is null)
        {
            Response.Headers.ETag = $"\"{answer.Version}\"";
        }

        return answer.Response is null ? StatusCode(StatusCodes.Status304NotModified) : Ok(answer.Response);
    }
}
