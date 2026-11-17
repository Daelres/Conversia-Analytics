using Conversia.API.Middleware;
using Conversia.Application.DTOs.Tenants;
using Conversia.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

/// <summary>The caller's own company. Answers any signed-in user of it, whatever its status.</summary>
[ApiController]
[Route("api/tenant")]
[Authorize]
public class TenantController : ControllerBase
{
    private readonly ITenantService _tenants;
    private readonly IAiAnalysisStatusQueries _aiStatus;
    private readonly IMessagingSettingsService _messagingSettings;

    public TenantController(ITenantService tenants, IAiAnalysisStatusQueries aiStatus, IMessagingSettingsService messagingSettings)
    {
        _tenants = tenants;
        _aiStatus = aiStatus;
        _messagingSettings = messagingSettings;
    }

    /// <summary>The company's messaging settings: whether read receipts and the typing indicator are on (docs/MESSAGING.md).</summary>
    [HttpGet("messaging-settings")]
    [Authorize(Policy = nameof(Domain.Enums.Permission.ConversationsView))]
    public async Task<ActionResult<Application.DTOs.Conversations.MessagingSettingsResponse>> GetMessagingSettings(CancellationToken cancellationToken)
        => Ok(await _messagingSettings.GetAsync(User.GetTenantId(), cancellationToken));

    /// <summary>
    /// Switches read receipts and the typing indicator for the whole company. They change what the customers see, so only who manages the
    /// organization (the owner always can) decides.
    /// </summary>
    [HttpPut("messaging-settings")]
    [Authorize(Policy = nameof(Domain.Enums.Permission.OrganizationManage))]
    public async Task<ActionResult<Application.DTOs.Conversations.MessagingSettingsResponse>> SetMessagingSettings(
        Application.DTOs.Conversations.SetMessagingSettingsRequest request, CancellationToken cancellationToken)
        => Ok(await _messagingSettings.SetAsync(User.GetTenantId(), request, User.GetUserId(), cancellationToken));

    /// <summary>Whether the company's AI is on and when it last updated the AI fields (a note of the dashboard, without technical detail).</summary>
    [HttpGet("ai-analysis")]
    [Authorize(Policy = nameof(Domain.Enums.Permission.MetricsView))]
    public async Task<ActionResult<Application.DTOs.Metrics.AiAnalysisStatusResponse>> GetAiAnalysis(CancellationToken cancellationToken)
        => Ok(await _aiStatus.GetAsync(cancellationToken));

    [HttpGet]
    [AllowInactiveTenant]
    public async Task<ActionResult<TenantInfoResponse>> GetTenant(CancellationToken cancellationToken)
        => Ok(await _tenants.GetCurrentAsync(User.GetTenantId(), cancellationToken));
}
