using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Administration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

/// <summary>
/// What the platform administrator does with the companies: approve or suspend them, set how many lines each may have active, switch a
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

    /// <summary>Forces a line off whatever the company wants. Idempotent.</summary>
    [HttpPost("tenants/{id:guid}/channels/{channelId:guid}/deactivate")]
    public async Task<ActionResult<TenantLineResponse>> DeactivateChannel(Guid id, Guid channelId, CancellationToken cancellationToken)
        => Ok(await _admin.DeactivateChannelAsync(AdminId, id, channelId, cancellationToken));

    [HttpGet("audit")]
    public async Task<ActionResult<PagedResponse<AuditEntryResponse>>> ListAudit([FromQuery] AdminAuditQuery query, CancellationToken cancellationToken)
        => Ok(await _admin.ListAuditAsync(query, cancellationToken));

    // The policy only passes with a valid administrator token, which always carries a sub.
    private Guid AdminId => User.GetUserId()!.Value;
}
