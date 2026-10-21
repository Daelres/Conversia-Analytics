using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;

namespace Conversia.Application.Services;

public class PlatformAdminService : IPlatformAdminService
{
    private const int RecentAuditCount = 10;

    private readonly IPlatformAdminQueries _queries;
    private readonly ITenantUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public PlatformAdminService(IPlatformAdminQueries queries, ITenantUnitOfWork unitOfWork, TimeProvider time)
    {
        _queries = queries;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    public async Task<PagedResponse<TenantSummaryResponse>> ListTenantsAsync(AdminTenantsQuery query, CancellationToken cancellationToken = default)
    {
        var filter = new TenantAdminFilter(ParseStatus(query.Status), string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim());
        var page = await _queries.ListTenantsAsync(filter, query.Page, query.PageSize, cancellationToken);
        return new PagedResponse<TenantSummaryResponse>
        {
            Items = page.Items.Select(TenantSummaryResponse.From).ToList(),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = page.TotalCount,
        };
    }

    public async Task<TenantDetailResponse> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var detail = await _queries.GetTenantAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException();
        var audit = await _queries.ListAuditAsync(tenantId, 1, RecentAuditCount, cancellationToken);

        var response = new TenantDetailResponse
        {
            Lines = detail.Lines.Select(TenantLineResponse.From).ToList(),
            RecentAudit = audit.Items.Select(AuditEntryResponse.From).ToList(),
        };
        Copy(TenantSummaryResponse.From(detail.Summary), response);
        return response;
    }

    public async Task<TenantSummaryResponse> ChangeStatusAsync(
        Guid adminId, Guid tenantId, ChangeTenantStatusRequest request, CancellationToken cancellationToken = default)
    {
        var newStatus = ParseStatus(request.Status) ?? throw new DomainException("El estado indicado no es válido.");

        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException())
        {
            var tenant = change.Tenant;
            var oldStatus = tenant.Status;
            if (!Tenant.CanChangeStatus(oldStatus, newStatus))
            {
                throw new TenantStatusConflictException(oldStatus, newStatus);
            }

            tenant.ChangeStatus(newStatus, adminId, request.Reason, _time.GetUtcNow().UtcDateTime);
            change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.TenantStatusChanged, tenantId, new Dictionary<string, object?>
            {
                ["from"] = oldStatus.ToString(),
                ["to"] = newStatus.ToString(),
                ["reason"] = tenant.StatusReason,
            }));
            await change.CommitAsync(cancellationToken);
        }

        return await ReadSummaryAsync(tenantId, cancellationToken);
    }

    public async Task<TenantSummaryResponse> SetLineQuotaAsync(
        Guid adminId, Guid tenantId, SetLineQuotaRequest request, CancellationToken cancellationToken = default)
    {
        var quota = request.MaxActiveLines ?? throw new DomainException(Tenant.MaxActiveLinesRuleMessage);

        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException())
        {
            var tenant = change.Tenant;
            var previous = tenant.MaxActiveLines;

            // Counted under the lock of the company's row, so a line switched on at this very moment is either counted or refused.
            var active = await change.CountActiveLinesAsync(cancellationToken);
            if (quota < active)
            {
                throw new LineQuotaBelowActiveException(active);
            }

            if (quota != previous)
            {
                tenant.SetMaxActiveLines(quota);
                change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.TenantQuotaChanged, tenantId, new Dictionary<string, object?>
                {
                    ["from"] = previous,
                    ["to"] = quota,
                }));
                await change.CommitAsync(cancellationToken);
            }
        }

        return await ReadSummaryAsync(tenantId, cancellationToken);
    }

    public async Task<TenantLineResponse> DeactivateChannelAsync(
        Guid adminId, Guid tenantId, Guid channelId, CancellationToken cancellationToken = default)
    {
        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException())
        {
            var channel = await change.GetChannelAsync(channelId, cancellationToken) ?? throw new ChannelNotFoundException();
            if (channel.IsActive)
            {
                channel.Deactivate();
                change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.ChannelDeactivated, tenantId, new Dictionary<string, object?>
                {
                    ["channelId"] = channel.Id,
                    ["displayPhoneNumber"] = channel.DisplayPhoneNumber,
                }));
                await change.CommitAsync(cancellationToken);
            }
        }

        var detail = await _queries.GetTenantAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException();
        return TenantLineResponse.From(detail.Lines.First(l => l.ChannelId == channelId));
    }

    public async Task<PagedResponse<AuditEntryResponse>> ListAuditAsync(AdminAuditQuery query, CancellationToken cancellationToken = default)
    {
        var page = await _queries.ListAuditAsync(query.TenantId, query.Page, query.PageSize, cancellationToken);
        return new PagedResponse<AuditEntryResponse>
        {
            Items = page.Items.Select(AuditEntryResponse.From).ToList(),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = page.TotalCount,
        };
    }

    private async Task<TenantSummaryResponse> ReadSummaryAsync(Guid tenantId, CancellationToken cancellationToken)
        => TenantSummaryResponse.From((await _queries.GetTenantAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException()).Summary);

    private static TenantStatus? ParseStatus(string? name)
        => Enum.TryParse<TenantStatus>(name, ignoreCase: true, out var status) && Enum.IsDefined(status) && !int.TryParse(name, out _) ? status : null;

    private static void Copy(TenantSummaryResponse from, TenantSummaryResponse to)
    {
        to.Id = from.Id;
        to.Name = from.Name;
        to.Status = from.Status;
        to.MaxActiveLines = from.MaxActiveLines;
        to.ActiveLines = from.ActiveLines;
        to.RegisteredLines = from.RegisteredLines;
        to.OwnerEmail = from.OwnerEmail;
        to.RegisteredAt = from.RegisteredAt;
        to.LastActivityAt = from.LastActivityAt;
        to.StatusChangedAt = from.StatusChangedAt;
        to.StatusReason = from.StatusReason;
    }
}
