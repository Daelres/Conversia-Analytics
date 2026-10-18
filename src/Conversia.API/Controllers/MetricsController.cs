using Conversia.Application.DTOs.Metrics;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

/// <summary>The dashboard's numbers (docs/METRICS.md). Every endpoint takes the same query: a period (<c>from</c> inclusive, <c>to</c> exclusive, local dates) and filters.</summary>
[ApiController]
[Route("api/metrics")]
[Authorize]
public class MetricsController : ControllerBase
{
    private readonly IMetricsService _metrics;

    public MetricsController(IMetricsService metrics)
    {
        _metrics = metrics;
    }

    [HttpGet("summary")]
    [Authorize(Policy = nameof(Permission.MetricsView))]
    public async Task<ActionResult<SummaryResponse>> GetSummary([FromQuery] MetricsQuery query, CancellationToken cancellationToken)
        => Ok(await _metrics.GetSummaryAsync(query, cancellationToken));

    [HttpGet("conversion")]
    [Authorize(Policy = nameof(Permission.MetricsView))]
    public async Task<ActionResult<ConversionResponse>> GetConversion([FromQuery] MetricsQuery query, CancellationToken cancellationToken)
        => Ok(await _metrics.GetConversionAsync(query, cancellationToken));

    [HttpGet("first-response")]
    [Authorize(Policy = nameof(Permission.MetricsView))]
    public async Task<ActionResult<FirstResponseResponse>> GetFirstResponse([FromQuery] MetricsQuery query, CancellationToken cancellationToken)
        => Ok(await _metrics.GetFirstResponseAsync(query, cancellationToken));

    [HttpGet("loss-reasons")]
    [Authorize(Policy = nameof(Permission.MetricsView))]
    public async Task<ActionResult<LossReasonsResponse>> GetLossReasons([FromQuery] MetricsQuery query, CancellationToken cancellationToken)
        => Ok(await _metrics.GetLossReasonsAsync(query, cancellationToken));

    [HttpGet("volume")]
    [Authorize(Policy = nameof(Permission.MetricsView))]
    public async Task<ActionResult<VolumeResponse>> GetVolume([FromQuery] MetricsQuery query, CancellationToken cancellationToken)
        => Ok(await _metrics.GetVolumeAsync(query, cancellationToken));

    [HttpGet("alerts")]
    [Authorize(Policy = nameof(Permission.MetricsView))]
    public async Task<ActionResult<AlertsResponse>> GetAlerts([FromQuery] MetricsQuery query, CancellationToken cancellationToken)
        => Ok(await _metrics.GetAlertsAsync(query, cancellationToken));
}
