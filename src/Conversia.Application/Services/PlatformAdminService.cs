using Conversia.Application.Classification.Llm;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Privacy;
using Conversia.Domain.Common;
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
    private readonly PlatformLimitDefaults _defaults;
    private readonly ITenantSendRateLimits? _sendLimits;
    private readonly ILineLicenseRepository? _licenses;
    private readonly TenantLifecycleService _lifecycle;

    /// <param name="defaults">The deployment defaults the company limits fall back to; the built-in ones when not given.</param>
    /// <param name="sendLimits">The send limiter of this instance, told at once of a new per-company limit.</param>
    /// <param name="licenses">The licence inventory, for the licences of a company in its detail.</param>
    /// <param name="lifecycle">Cancelling and deleting a company (retention window, deletion job); the defaults when not given.</param>
    public PlatformAdminService(
        IPlatformAdminQueries queries, ITenantUnitOfWork unitOfWork, TimeProvider time, PlatformLimitDefaults? defaults = null,
        ITenantSendRateLimits? sendLimits = null, ILineLicenseRepository? licenses = null, TenantLifecycleService? lifecycle = null)
    {
        _lifecycle = lifecycle ?? new TenantLifecycleService(unitOfWork, time, new PrivacyHasher(PrivacyHashKeys.DevelopmentFallback()));
        _queries = queries;
        _unitOfWork = unitOfWork;
        _time = time;
        _defaults = defaults ?? new PlatformLimitDefaults(120, AiQuotaSettings.Default, 1200);
        _sendLimits = sendLimits;
        _licenses = licenses;
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

        var licenses = _licenses is null
            ? []
            : (await _licenses.ListAsync(new LineLicenseFilter(LineLicenseStatus.Assigned, tenantId), 1, AdminTenantsQuery.MaxPageSize, cancellationToken)).Items;
        var response = new TenantDetailResponse
        {
            Lines = detail.Lines.Select(TenantLineResponse.From).ToList(),
            RecentAudit = audit.Items.Select(AuditEntryResponse.From).ToList(),
            Limits = LimitsOf(detail.Summary),
            Licenses = licenses.Select(LineLicenseResponse.From).ToList(),
            DeletionRetentionDays = _lifecycle.Settings.DeletionRetentionDays,
        };
        Copy(TenantSummaryResponse.From(detail.Summary), response);
        return response;
    }

    public async Task<TenantSummaryResponse> ChangeStatusAsync(
        Guid adminId, Guid tenantId, ChangeTenantStatusRequest request, CancellationToken cancellationToken = default)
    {
        var newStatus = ParseStatus(request.Status) ?? throw new DomainException("El estado indicado no es válido.");
        var reasonCode = AdminReasonCodes.Optional(request.ReasonCode);

        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException())
        {
            var tenant = change.Tenant;
            var oldStatus = tenant.Status;
            var oldCause = tenant.SuspensionCause;
            if (!Tenant.CanChangeStatus(oldStatus, tenant.SuspensionCause, newStatus))
            {
                throw new TenantStatusConflictException(oldStatus, newStatus);
            }

            if (!Tenant.CanReactivate(tenant))
            {
                throw new TenantLifecycleConflictException(TenantLifecycleConflictException.DeletionStarted);
            }

            if (newStatus == TenantStatus.Cancelled)
            {
                // Lines off, licences released, deletion scheduled: the same as the billing job's cancellation, audited with the administrator.
                await _lifecycle.CancelLockedAsync(change, adminId, request.Reason, reasonCode, cancellationToken);
            }
            else
            {
                tenant.ChangeStatus(newStatus, adminId, request.Reason, _time.GetUtcNow().UtcDateTime);
                change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.TenantStatusChanged, tenantId, new Dictionary<string, object?>
                {
                    ["from"] = oldStatus.ToString(),
                    ["to"] = tenant.Status.ToString(), // a pause resumed with an unpaid hold lands in the billing suspension
                    ["reason"] = tenant.StatusReason,
                    ["cause"] = tenant.SuspensionCause?.ToString(),
                    ["previousCause"] = oldCause?.ToString(),
                }, reasonCode));
            }

            await change.CommitAsync(cancellationToken);
        }

        return await ReadSummaryAsync(tenantId, cancellationToken);
    }

    /// <summary>The audit actions that make up a company's lifecycle history.</summary>
    public static readonly IReadOnlyList<string> LifecycleActions =
    [
        PlatformAuditLog.Actions.TenantStatusChanged,
        PlatformAuditLog.Actions.TenantDataDeletionStarted,
        PlatformAuditLog.Actions.TenantDataDeleted,
    ];

    public const string ConfirmNameMismatchMessage = "El nombre escrito no coincide con el nombre de la empresa. Escríbelo tal como aparece.";

    public static readonly string DeletionReasonRuleMessage = $"El motivo {TextRules.RuleText(DeleteTenantDataRequest.ReasonMaxLength)}.";

    public async Task<TenantSummaryResponse> RequestDataDeletionAsync(
        Guid adminId, Guid tenantId, DeleteTenantDataRequest request, CancellationToken cancellationToken = default)
    {
        var reason = TextRules.Required(request.Reason, DeleteTenantDataRequest.ReasonMaxLength, DeletionReasonRuleMessage);
        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException())
        {
            var tenant = change.Tenant;
            if (tenant.Status != TenantStatus.Cancelled)
            {
                throw new TenantLifecycleConflictException(TenantLifecycleConflictException.NotCancelled);
            }

            // The strong confirmation: the administrator types the name of the company whose data goes (case and outer spaces aside).
            if (!string.Equals(request.ConfirmName?.Trim(), tenant.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException(ConfirmNameMismatchMessage);
            }

            if (await _lifecycle.BeginDeletionLockedAsync(
                    change, adminId, reason, TenantDeletionTrigger.Manual, ReasonCodes.DeletionRequestedByAdmin, cancellationToken))
            {
                await change.CommitAsync(cancellationToken);
            }
        }

        _lifecycle.NotifyDeletionJob();
        return await ReadSummaryAsync(tenantId, cancellationToken);
    }

    public async Task<PagedResponse<AuditEntryResponse>> ListLifecycleAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var entries = await _queries.ListAuditAsync(tenantId, page, pageSize, cancellationToken, LifecycleActions);
        return new PagedResponse<AuditEntryResponse>
        {
            Items = entries.Items.Select(AuditEntryResponse.From).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = entries.TotalCount,
        };
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

    public async Task<TenantSummaryResponse> SetSandboxModeAsync(
        Guid adminId, Guid tenantId, SetSandboxModeRequest request, CancellationToken cancellationToken = default)
    {
        var enabled = request.Enabled ?? throw new DomainException("Indica si el modo sandbox queda activado o desactivado.");
        var reason = Tenant.SandboxModeReason(request.Reason);

        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException())
        {
            var tenant = change.Tenant;
            var previous = tenant.SandboxMode;
            if (tenant.SetSandboxMode(enabled))
            {
                change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.TenantSandboxModeChanged, tenantId, new Dictionary<string, object?>
                {
                    ["from"] = previous,
                    ["to"] = enabled,
                    ["reason"] = reason,
                }));
                await change.CommitAsync(cancellationToken);
            }
        }

        return await ReadSummaryAsync(tenantId, cancellationToken);
    }

    public async Task<TenantSummaryResponse> SetAiEnabledAsync(
        Guid adminId, Guid tenantId, SetAiEnabledRequest request, CancellationToken cancellationToken = default)
    {
        var enabled = request.Enabled ?? throw new DomainException("Indica si la IA queda activada o desactivada.");
        var reason = Tenant.AiChangeReason(request.Reason);

        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException())
        {
            var tenant = change.Tenant;
            var previous = tenant.AiEnabled;
            var previousTerms = tenant.AiTermsVersion;
            if (tenant.SetAiEnabled(enabled, request.TermsVersion, _time.GetUtcNow().UtcDateTime))
            {
                // Consent evidence: who, when, why and which terms. Never conversation content.
                change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.TenantAiEnabledChanged, tenantId, new Dictionary<string, object?>
                {
                    ["from"] = previous,
                    ["to"] = enabled,
                    ["reason"] = reason,
                    ["termsVersion"] = enabled ? tenant.AiTermsVersion : null,
                    ["previousTermsVersion"] = previousTerms,
                }));
                await change.CommitAsync(cancellationToken);
            }
        }

        return await ReadSummaryAsync(tenantId, cancellationToken);
    }

    public async Task<TenantSummaryResponse> SetAiQuotaAsync(
        Guid adminId, Guid tenantId, SetAiQuotaRequest request, CancellationToken cancellationToken = default)
    {
        var reason = Tenant.LimitChangeReason(request.Reason);
        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException())
        {
            var tenant = change.Tenant;
            var previous = tenant.AiMonthlyConversationQuota;
            tenant.SetAiMonthlyConversationQuota(request.MonthlyConversations);
            if (previous != request.MonthlyConversations)
            {
                change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.TenantAiQuotaChanged, tenantId, new Dictionary<string, object?>
                {
                    ["from"] = previous,
                    ["to"] = request.MonthlyConversations,
                    ["reason"] = reason,
                }));
                await change.CommitAsync(cancellationToken);
            }
        }

        return await ReadSummaryAsync(tenantId, cancellationToken);
    }

    public async Task<TenantSummaryResponse> SetSendRateLimitAsync(
        Guid adminId, Guid tenantId, SetSendRateLimitRequest request, CancellationToken cancellationToken = default)
    {
        var reason = Tenant.LimitChangeReason(request.Reason);
        var changed = false;
        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException())
        {
            var tenant = change.Tenant;
            var previous = tenant.SendRateLimitPerMinute;
            tenant.SetSendRateLimitPerMinute(request.PerMinute, _defaults.MaxSendRateLimitPerMinute);
            if (previous != request.PerMinute)
            {
                change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.TenantSendRateLimitChanged, tenantId, new Dictionary<string, object?>
                {
                    ["from"] = previous,
                    ["to"] = request.PerMinute,
                    ["reason"] = reason,
                }));
                await change.CommitAsync(cancellationToken);
                changed = true;
            }
        }

        if (changed)
        {
            // This instance applies it to the very next send; the others when they reload the overrides.
            _sendLimits?.Update(tenantId, request.PerMinute);
        }

        return await ReadSummaryAsync(tenantId, cancellationToken);
    }

    private TenantLimitsResponse LimitsOf(TenantAdminSummary s)
    {
        var aiQuota = _defaults.Ai.EffectiveQuota(s.AiMonthlyConversationQuota);
        return new TenantLimitsResponse
        {
            MaxActiveLines = s.MaxActiveLines,
            ActiveLines = s.ActiveLines,
            AiEnabled = s.AiEnabled,
            AiTermsVersion = s.AiTermsVersion,
            AiEnabledChangedAt = s.AiEnabledChangedAt,
            AiMonthlyConversationQuota = aiQuota,
            AiQuotaIsDefault = s.AiMonthlyConversationQuota is null,
            DefaultAiMonthlyConversationQuota = _defaults.Ai.DefaultMonthlyConversations,
            AiMonthlyUsdCeiling = _defaults.Ai.CeilingUsd(aiQuota),
            SendRateLimitPerMinute = s.SendRateLimitPerMinute ?? _defaults.TenantSendRateLimitPerMinute,
            SendRateLimitIsDefault = s.SendRateLimitPerMinute is null,
            DefaultSendRateLimitPerMinute = _defaults.TenantSendRateLimitPerMinute,
            MaxSendRateLimitPerMinute = _defaults.MaxSendRateLimitPerMinute,
        };
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
        to.SandboxMode = from.SandboxMode;
        to.AiEnabled = from.AiEnabled;
        to.SuspensionCause = from.SuspensionCause;
        to.StatusReasonCode = from.StatusReasonCode;
        to.CancelledAt = from.CancelledAt;
        to.DataDeletionDueAt = from.DataDeletionDueAt;
        to.DataDeletionStartedAt = from.DataDeletionStartedAt;
    }
}
