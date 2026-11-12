using Conversia.API.RateLimiting;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.Controllers;

[ApiController]
[Route("api/conversations")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly IConversationService _conversations;
    private readonly IMessageSendingService _messages;

    public ConversationsController(IConversationService conversations, IMessageSendingService messages)
    {
        _conversations = conversations;
        _messages = messages;
    }

    /// <summary>The caller's conversations, newest first, filtered by branch, advisor, stage, source, status, start date and whether they are unanswered.</summary>
    [HttpGet]
    [Authorize(Policy = nameof(Permission.ConversationsView))]
    [EnableRateLimiting(HeavyQueryRateLimitingExtensions.PolicyName)]
    public async Task<ActionResult<PagedResponse<ConversationListItem>>> GetConversations([FromQuery] ConversationQuery query, CancellationToken cancellationToken)
        => Ok(await _conversations.ListAsync(query, cancellationToken));

    /// <summary>One conversation with its opportunity and its messages (text included: it is the tenant's own data), oldest first.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = nameof(Permission.ConversationsView))]
    public async Task<ActionResult<ConversationDetailResponse>> GetConversation(Guid id, CancellationToken cancellationToken)
        => Ok(await _conversations.GetAsync(id, cancellationToken));

    /// <summary>Assigns the advisor of the conversation (an advisor of its branch), or unassigns with <c>{"advisorId": null}</c>.</summary>
    [HttpPut("{id:guid}/advisor")]
    [Authorize(Policy = nameof(Permission.ConversationsManage))]
    public async Task<ActionResult<ConversationListItem>> AssignAdvisor(Guid id, AssignAdvisorRequest request, CancellationToken cancellationToken)
        => Ok(await _conversations.AssignAdvisorAsync(id, request, User.GetUserId(), cancellationToken));

    /// <summary>A person decides the outcome (Won, Lost with a reason, or InFollowUp). The classifier never overwrites it.</summary>
    [HttpPut("{id:guid}/opportunity")]
    [Authorize(Policy = nameof(Permission.ConversationsManage))]
    public async Task<ActionResult<OpportunityResponse>> SetOutcome(Guid id, SetOpportunityOutcomeRequest request, CancellationToken cancellationToken)
        => Ok(await _conversations.SetOutcomeAsync(id, request, User.GetUserId(), cancellationToken));

    /// <summary>
    /// A person sends a plain-text WhatsApp message to the customer of the conversation, through the conversation's channel and its
    /// provider credentials. 201 with the stored Outbound message once the provider accepted it; every refusal has its own code (docs/MESSAGING.md).
    /// </summary>
    [HttpPost("{id:guid}/messages")]
    [Authorize(Policy = nameof(Permission.ConversationsManage))]
    [EnableRateLimiting(MessageSendRateLimitingExtensions.PolicyName)]
    public async Task<ActionResult<MessageResponse>> SendMessage(Guid id, SendMessageRequest request, CancellationToken cancellationToken)
        => StatusCode(StatusCodes.Status201Created, await _messages.SendTextAsync(id, request, cancellationToken));
}
