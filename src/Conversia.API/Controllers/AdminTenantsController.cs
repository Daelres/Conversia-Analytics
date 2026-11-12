using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Administration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

/// <summary>
/// What the platform administrator does with the companies: approve, pause, suspend, cancel or delete them, set how many lines each may have active, switch a
/// line off. Business facts only: there is no endpoint here (or anywhere for this role) that returns conversations, messages, contacts,
/// metrics or a company's users.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = PlatformAdminTokens.PolicyName)]
public class AdminTenantsController : ControllerBase
{
    private readonly IPlatformAdminService _admin;

    public AdminTenantsController(IPlatformAdminService admin)
    {
        _admin = admin;
    }

    [HttpGet("tenants")]
    public async Task<ActionResult<PagedResponse<TenantSummaryResponse>>> ListTenants([FromQuery] AdminTenantsQuery query, CancellationToken cancellationToken)
        => Ok(await _admin.ListTenantsAsync(query, cancellationToken));

    [HttpGet("tenants/{id:guid}")]
    public async Task<ActionResult<TenantDetailResponse>> GetTenant(Guid id, CancellationToken cancellationToken)
        => Ok(await _admin.GetTenantAsync(id, cancellationToken));

    [HttpPut("tenants/{id:guid}/status")]
    public async Task<ActionResult<TenantSummaryResponse>> ChangeStatus(Guid id, ChangeTenantStatusRequest request, CancellationToken cancellationToken)
        => Ok(await _admin.ChangeStatusAsync(AdminId, id, request, cancellationToken));

    [HttpPut("tenants/{id:guid}/line-quota")]
    public async Task<ActionResult<TenantSummaryResponse>> SetLineQuota(Guid id, SetLineQuotaRequest request, CancellationToken cancellationToken)
        => Ok(await _admin.SetLineQuotaAsync(AdminId, id, request, cancellationToken));

    /// <summary>
    /// Switches the internal sandbox of a company on or off (docs/SANDBOX.md); the reason is required and audited. Idempotent. This is the
    /// only way the mode changes: no endpoint of a company's own API can.
    /// </summary>
    [HttpPut("tenants/{id:guid}/sandbox-mode")]
    public async Task<ActionResult<TenantSummaryResponse>> SetSandboxMode(Guid id, SetSandboxModeRequest request, CancellationToken cancellationToken)
        => Ok(await _admin.SetSandboxModeAsync(AdminId, id, request, cancellationToken));

    /// <summary>
    /// Switches the AI on or off for a company (docs/CLASSIFICATION.md, "Usage and quota"): the reason is required, and the version of the terms
    /// the company accepted is required to switch it on. Audited (<c>tenant.ai_enabled_changed</c>). Idempotent.
    /// </summary>
    [HttpPut("tenants/{id:guid}/ai-enabled")]
    public async Task<ActionResult<TenantSummaryResponse>> SetAiEnabled(Guid id, SetAiEnabledRequest request, CancellationToken cancellationToken)
        => Ok(await _admin.SetAiEnabledAsync(AdminId, id, request, cancellationToken));

    /// <summary>The company's monthly AI quota (0 to 1,000,000 distinct conversations); null resets it to the deployment default.</summary>
    [HttpPut("tenants/{id:guid}/ai-quota")]
    public async Task<ActionResult<TenantSummaryResponse>> SetAiQuota(Guid id, SetAiQuotaRequest request, CancellationToken cancellationToken)
        => Ok(await _admin.SetAiQuotaAsync(AdminId, id, request, cancellationToken));

    /// <summary>The company's send rate limit per minute (all its users together); null resets it to <c>Messaging:TenantSendRateLimitPerMinute</c>.</summary>
    [HttpPut("tenants/{id:guid}/send-rate-limit")]
    public async Task<ActionResult<TenantSummaryResponse>> SetSendRateLimit(Guid id, SetSendRateLimitRequest request, CancellationToken cancellationToken)
        => Ok(await _admin.SetSendRateLimitAsync(AdminId, id, request, cancellationToken));

    /// <summary>Forces a line off whatever the company wants. Idempotent.</summary>
    [HttpPost("tenants/{id:guid}/channels/{channelId:guid}/deactivate")]
    public async Task<ActionResult<TenantLineResponse>> DeactivateChannel(Guid id, Guid channelId, CancellationToken cancellationToken)
        => Ok(await _admin.DeactivateChannelAsync(AdminId, id, channelId, cancellationToken));

    /// <summary>
    /// Deletes a cancelled company's data now instead of at the end of its retention window (Ley 1581): the body carries the company's name
    /// typed by the administrator and the reason. <c>202</c>: the deletion job does it in the background (the company shows when it started);
    /// asking again changes nothing. <c>409</c> <c>tenant_not_cancelled</c> for a company that is not cancelled.
    /// </summary>
    [HttpPost("tenants/{id:guid}/data-deletion")]
    public async Task<ActionResult<TenantSummaryResponse>> RequestDataDeletion(Guid id, DeleteTenantDataRequest request, CancellationToken cancellationToken)
        => Accepted(await _admin.RequestDataDeletionAsync(AdminId, id, request, cancellationToken));

    /// <summary>The company's lifecycle history (status changes and data deletion) from the audit trail, newest first.</summary>
    [HttpGet("tenants/{id:guid}/lifecycle")]
    public async Task<ActionResult<PagedResponse<AuditEntryResponse>>> ListLifecycle(Guid id, [FromQuery] AdminAuditQuery query, CancellationToken cancellationToken)
        => Ok(await _admin.ListLifecycleAsync(id, query.Page, query.PageSize, cancellationToken));

    [HttpGet("audit")]
    public async Task<ActionResult<PagedResponse<AuditEntryResponse>>> ListAudit([FromQuery] AdminAuditQuery query, CancellationToken cancellationToken)
        => Ok(await _admin.ListAuditAsync(query, cancellationToken));

    // The policy only passes with a valid administrator token, which always carries a sub.
    private Guid AdminId => User.GetUserId()!.Value;
}
