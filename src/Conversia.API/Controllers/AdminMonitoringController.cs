using Conversia.Application.DTOs.Admin;
using Conversia.Application.Monitoring;
using Conversia.Application.Reports;
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
    private readonly IReportMonitoringQueries _reports;

    public AdminMonitoringController(IPlatformMonitoringService monitoring, IReportMonitoringQueries reports)
    {
        _monitoring = monitoring;
        _reports = reports;
    }

    /// <summary>The manager reports of this month per company (docs/PLATFORM_ADMIN.md, "Manager reports"): counts, model share and cost, and the email sender.</summary>
    [HttpGet("monitoring/reports")]
    public async Task<ActionResult<ReportMonitoringResponse>> Reports(CancellationToken cancellationToken)
        => Ok(await _reports.GetAsync(cancellationToken));

    [HttpGet("monitoring")]
    public async Task<ActionResult<PlatformOverviewResponse>> Overview(CancellationToken cancellationToken)
        => Ok(await _monitoring.GetOverviewAsync(cancellationToken));

    [HttpGet("tenants/{id:guid}/monitoring")]
    public async Task<ActionResult<TenantMonitoringResponse>> Tenant(Guid id, CancellationToken cancellationToken)
        => Ok(await _monitoring.GetTenantAsync(id, cancellationToken));
}
