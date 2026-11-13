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

    public TenantController(ITenantService tenants, IAiAnalysisStatusQueries aiStatus)
    {
        _tenants = tenants;
        _aiStatus = aiStatus;
    }

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
