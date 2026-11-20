using Conversia.Application.Assistant;
using Conversia.Application.DTOs.Assistant;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

/// <summary>
/// The company's AI sales assistant (docs/ASSISTANT.md). Settings, lines and knowledge base need <c>AssistantManage</c>; switching it on is
/// the owner's alone (with the terms and the legal-basis acknowledgement); taking a conversation over or handing it back needs
/// <c>ConversationsManage</c>, reading its state <c>ConversationsView</c>.
/// </summary>
[ApiController]
[Authorize]
public class AssistantController(IAssistantService assistant) : ControllerBase
{
    private Guid CallerId => User.GetUserId() ?? Guid.Empty;

    [HttpGet("api/assistant/settings")]
    [Authorize(Policy = nameof(Permission.AssistantManage))]
    public async Task<ActionResult<AssistantSettingsResponse>> GetSettings(CancellationToken cancellationToken)
        => Ok(await assistant.GetSettingsAsync(User.GetTenantId(), cancellationToken));

    [HttpPut("api/assistant/settings")]
    [Authorize(Policy = nameof(Permission.AssistantManage))]
    public async Task<ActionResult<AssistantSettingsResponse>> UpdateSettings(UpdateAssistantSettingsRequest request, CancellationToken cancellationToken)
        => Ok(await assistant.UpdateSettingsAsync(User.GetTenantId(), request, CallerId, cancellationToken));

    /// <summary>The owner switches it on, accepting the current terms. <c>403</c> for anyone else, even with <c>AssistantManage</c>.</summary>
    [HttpPost("api/assistant/enable")]
    [Authorize(Policy = nameof(Permission.AssistantManage))]
    public async Task<ActionResult<AssistantSettingsResponse>> Enable(EnableAssistantRequest request, CancellationToken cancellationToken)
    {
        if (User.FindFirst("is_owner")?.Value != "true" || User.GetUserId() is not { } owner)
        {
            return Forbid();
        }

        return Ok(await assistant.EnableAsync(User.GetTenantId(), request, owner, cancellationToken));
    }

    [HttpPost("api/assistant/disable")]
    [Authorize(Policy = nameof(Permission.AssistantManage))]
    public async Task<ActionResult<AssistantSettingsResponse>> Disable(CancellationToken cancellationToken)
        => Ok(await assistant.DisableAsync(User.GetTenantId(), CallerId, cancellationToken));

    [HttpPut("api/assistant/channels/{channelId:guid}")]
    [Authorize(Policy = nameof(Permission.AssistantManage))]
    public async Task<ActionResult<AssistantSettingsResponse>> SetChannel(Guid channelId, SetChannelAssistantRequest request, CancellationToken cancellationToken)
        => Ok(await assistant.SetChannelAsync(User.GetTenantId(), channelId, request.Enabled!.Value, CallerId, cancellationToken));

    [HttpGet("api/assistant/knowledge")]
    [Authorize(Policy = nameof(Permission.AssistantManage))]
    public async Task<ActionResult<KnowledgeListResponse>> ListKnowledge(CancellationToken cancellationToken)
        => Ok(await assistant.ListKnowledgeAsync(cancellationToken));

    [HttpGet("api/assistant/knowledge/{number:int}")]
    [Authorize(Policy = nameof(Permission.AssistantManage))]
    public async Task<ActionResult<KnowledgeVersionResponse>> GetKnowledge(int number, CancellationToken cancellationToken)
        => Ok(await assistant.GetKnowledgeAsync(number, cancellationToken));

    /// <summary>Saves the whole knowledge base as a new draft version (<c>201</c>); the assistant keeps using the published one.</summary>
    [HttpPost("api/assistant/knowledge")]
    [Authorize(Policy = nameof(Permission.AssistantManage))]
    public async Task<ActionResult<KnowledgeVersionResponse>> SaveKnowledge(SaveKnowledgeRequest request, CancellationToken cancellationToken)
    {
        var saved = await assistant.SaveKnowledgeAsync(User.GetTenantId(), request.Content!, CallerId, cancellationToken);
        return CreatedAtAction(nameof(GetKnowledge), new { number = saved.Number }, saved);
    }

    [HttpPost("api/assistant/knowledge/{number:int}/publish")]
    [Authorize(Policy = nameof(Permission.AssistantManage))]
    public async Task<ActionResult<KnowledgeVersionResponse>> PublishKnowledge(int number, CancellationToken cancellationToken)
        => Ok(await assistant.PublishKnowledgeAsync(User.GetTenantId(), number, CallerId, cancellationToken));

    /// <summary>The append-only history of the assistant's settings and consent, newest first.</summary>
    [HttpGet("api/assistant/audit")]
    [Authorize(Policy = nameof(Permission.AssistantManage))]
    public async Task<ActionResult<IReadOnlyList<AssistantAuditEntryResponse>>> ListAudit(CancellationToken cancellationToken)
        => Ok(await assistant.ListAuditAsync(cancellationToken));

    [HttpGet("api/conversations/{id:guid}/assistant")]
    [Authorize(Policy = nameof(Permission.ConversationsView))]
    public async Task<ActionResult<ConversationAssistantResponse>> GetConversation(Guid id, CancellationToken cancellationToken)
        => Ok(await assistant.GetConversationAsync(id, cancellationToken));

    [HttpPost("api/conversations/{id:guid}/assistant/take-over")]
    [Authorize(Policy = nameof(Permission.ConversationsManage))]
    public async Task<ActionResult<ConversationAssistantResponse>> TakeOver(Guid id, CancellationToken cancellationToken)
        => Ok(await assistant.TakeOverAsync(User.GetTenantId(), id, CallerId, cancellationToken));

    [HttpPost("api/conversations/{id:guid}/assistant/hand-back")]
    [Authorize(Policy = nameof(Permission.ConversationsManage))]
    public async Task<ActionResult<ConversationAssistantResponse>> HandBack(Guid id, CancellationToken cancellationToken)
        => Ok(await assistant.HandBackAsync(User.GetTenantId(), id, CallerId, cancellationToken));
}
