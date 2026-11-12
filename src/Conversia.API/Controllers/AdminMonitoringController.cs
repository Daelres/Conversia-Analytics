using Conversia.Application.DTOs.Admin;
using Conversia.Application.Monitoring;
using Conversia.Infrastructure.Administration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

/// <summary>
/// How the service runs, without any private content (docs/PLATFORM_ADMIN.md, "Monitoring"): health, queues, deliveries, classification, AI
/// usage, errors by fingerprint and send outcomes, platform-wide and per company. Counts, statuses, codes and money only.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = PlatformAdminTokens.PolicyName)]
public class AdminMonitoringController : ControllerBase
{
    private readonly IPlatformMonitoringService _monitoring;

    public AdminMonitoringController(IPlatformMonitoringService monitoring)
    {
        _monitoring = monitoring;
    }

    [HttpGet("monitoring")]
    public async Task<ActionResult<PlatformOverviewResponse>> Overview(CancellationToken cancellationToken)
        => Ok(await _monitoring.GetOverviewAsync(cancellationToken));

    [HttpGet("tenants/{id:guid}/monitoring")]
    public async Task<ActionResult<TenantMonitoringResponse>> Tenant(Guid id, CancellationToken cancellationToken)
        => Ok(await _monitoring.GetTenantAsync(id, cancellationToken));
}
