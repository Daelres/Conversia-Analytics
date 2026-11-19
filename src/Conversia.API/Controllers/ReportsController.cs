using Conversia.API.Middleware;
using Conversia.API.RateLimiting;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Reports;
using Conversia.Application.Exceptions;
using Conversia.Application.Reports;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.Controllers;

/// <summary>
/// The automatic manager reports of the caller's company (docs/REPORTS.md). Definitions and preview need <c>ReportsManage</c>; the history needs
/// <c>MetricsView</c>; a report is readable with either, or by a person who receives it; the WhatsApp opt-in is each person's own; the unsubscribe
/// link is anonymous (its token names the recipient).
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly IReportService _reports;
    private readonly IAuthorizationService _authorization;

    public ReportsController(IReportService reports, IAuthorizationService authorization)
    {
        _reports = reports;
        _authorization = authorization;
    }

    // [Authorize] guarantees a valid company token, which always carries the user id.
    private Guid CallerId => User.GetUserId() ?? Guid.Empty;

    [HttpGet("definitions")]
    [Authorize(Policy = nameof(Permission.ReportsManage))]
    public async Task<ActionResult<IReadOnlyList<ReportDefinitionResponse>>> ListDefinitions(CancellationToken cancellationToken)
        => Ok(await _reports.ListDefinitionsAsync(cancellationToken));

    [HttpGet("definitions/{id:guid}")]
    [Authorize(Policy = nameof(Permission.ReportsManage))]
    public async Task<ActionResult<ReportDefinitionResponse>> GetDefinition(Guid id, CancellationToken cancellationToken)
        => Ok(await _reports.GetDefinitionAsync(id, cancellationToken));

    [HttpPost("definitions")]
    [Authorize(Policy = nameof(Permission.ReportsManage))]
    public async Task<ActionResult<ReportDefinitionResponse>> CreateDefinition(ReportDefinitionRequest request, CancellationToken cancellationToken)
    {
        var created = await _reports.CreateDefinitionAsync(User.GetTenantId(), request, CallerId, cancellationToken);
        return CreatedAtAction(nameof(GetDefinition), new { id = created.Id }, created);
    }

    [HttpPut("definitions/{id:guid}")]
    [Authorize(Policy = nameof(Permission.ReportsManage))]
    public async Task<ActionResult<ReportDefinitionResponse>> UpdateDefinition(Guid id, ReportDefinitionRequest request, CancellationToken cancellationToken)
        => Ok(await _reports.UpdateDefinitionAsync(User.GetTenantId(), id, request, CallerId, cancellationToken));

    [HttpDelete("definitions/{id:guid}")]
    [Authorize(Policy = nameof(Permission.ReportsManage))]
    public async Task<IActionResult> DeleteDefinition(Guid id, CancellationToken cancellationToken)
    {
        await _reports.DeleteDefinitionAsync(User.GetTenantId(), id, CallerId, cancellationToken);
        return NoContent();
    }

    /// <summary>The report the definition would send now, for the last complete period: written (maybe by the model), not stored, not sent.</summary>
    [HttpPost("definitions/{id:guid}/preview")]
    [Authorize(Policy = nameof(Permission.ReportsManage))]
    [EnableRateLimiting(ReportRateLimitingExtensions.PreviewPolicy)]
    public async Task<ActionResult<ReportPreviewResponse>> Preview(Guid id, CancellationToken cancellationToken)
        => Ok(await _reports.PreviewAsync(User.GetTenantId(), id, cancellationToken));

    [HttpGet("options")]
    [Authorize(Policy = nameof(Permission.ReportsManage))]
    public async Task<ActionResult<ReportOptionsResponse>> Options(CancellationToken cancellationToken)
        => Ok(await _reports.GetOptionsAsync(cancellationToken));

    [HttpGet]
    [Authorize(Policy = nameof(Permission.MetricsView))]
    public async Task<ActionResult<PagedResponse<ReportSummaryResponse>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
        => Ok(await _reports.ListReportsAsync(page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ReportResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var canReadAll = (await _authorization.AuthorizeAsync(User, nameof(Permission.MetricsView))).Succeeded
            || (await _authorization.AuthorizeAsync(User, nameof(Permission.ReportsManage))).Succeeded;
        return Ok(await _reports.GetReportAsync(id, CallerId, canReadAll, cancellationToken));
    }

    [HttpGet("whatsapp-opt-in")]
    public async Task<ActionResult<ReportWhatsAppOptInResponse>> GetOptIn(CancellationToken cancellationToken)
        => Ok(await _reports.GetWhatsAppOptInAsync(CallerId, cancellationToken));

    /// <summary>The caller accepts, on their own, to receive the reports on their WhatsApp number (recorded with the instant).</summary>
    [HttpPut("whatsapp-opt-in")]
    public async Task<ActionResult<ReportWhatsAppOptInResponse>> OptIn(ReportWhatsAppOptInRequest request, CancellationToken cancellationToken)
        => Ok(await _reports.OptInAsync(User.GetTenantId(), CallerId, request, cancellationToken));

    /// <summary>The code sent to the number: once it comes back, the number receives the reports.</summary>
    [HttpPost("whatsapp-opt-in/verify")]
    public async Task<ActionResult<ReportWhatsAppOptInResponse>> VerifyOptIn(ReportWhatsAppVerifyRequest request, CancellationToken cancellationToken)
        => Ok(await _reports.VerifyWhatsAppAsync(User.GetTenantId(), CallerId, request, cancellationToken));

    [HttpDelete("whatsapp-opt-in")]
    public async Task<ActionResult<ReportWhatsAppOptInResponse>> OptOut(CancellationToken cancellationToken)
        => Ok(await _reports.OptOutAsync(User.GetTenantId(), CallerId, cancellationToken));

    /// <summary>The unsubscribe link of a report email (also RFC 8058 one-click, through the Web). A wrong token is a 404, like a deleted one.</summary>
    /// <remarks>
    /// The token is in the path so the rate limiter can check its signature first: a valid link has a budget of its own, and invalid ones share
    /// another, so guessing never blocks a real unsubscribe. An invalid token never reaches the database.
    /// </remarks>
    [HttpPost("unsubscribe/{token}")]
    [AllowAnonymous]
    [AllowInactiveTenant]
    [EnableRateLimiting(ReportRateLimitingExtensions.UnsubscribePolicy)]
    public async Task<ActionResult<ReportUnsubscribeResponse>> Unsubscribe([FromRoute] string token, CancellationToken cancellationToken)
        => Ok(await _reports.UnsubscribeAsync(token, cancellationToken) ?? throw new ReportNotFoundException(ReportNotFoundException.InvalidLinkMessage));
}
