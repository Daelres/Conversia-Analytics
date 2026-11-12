using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Privacy;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

/// <summary><c>Tenants:Deletion:RetentionDays</c>: the days a cancelled company's data is kept before it is deleted (default 30).</summary>
public sealed record TenantLifecycleSettings
{
    public TenantLifecycleSettings(int deletionRetentionDays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(deletionRetentionDays, Tenant.MinDeletionRetentionDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(deletionRetentionDays, Tenant.MaxDeletionRetentionDays);
        DeletionRetentionDays = deletionRetentionDays;
    }

    public static TenantLifecycleSettings Default { get; } = new(Tenant.DefaultDeletionRetentionDays);

    public int DeletionRetentionDays { get; }
}

/// <summary>
/// The lifecycle changes of a company that are not a plain status flip (docs/PLATFORM_ADMIN.md, "Lifecycle of a company"): cancelling (lines
/// off, licences released, deletion scheduled), the billing hooks and the start of the data deletion. Every change runs under the lock of
/// the company's row (<see cref="ITenantUnitOfWork"/>), so it never interleaves with a line being switched on or another status change.
/// </summary>
public sealed class TenantLifecycleService : ITenantBillingLifecycle, ITenantDeletionScheduler
{
    private readonly ITenantUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly TenantLifecycleSettings _settings;
    private readonly ITenantDeletionSignal? _signal;
    private readonly PrivacyHasher _hasher;

    /// <param name="hasher">Keys the hash of the company name kept in the tombstone (<c>Privacy:HashKey</c>).</param>
    public TenantLifecycleService(
        ITenantUnitOfWork unitOfWork, TimeProvider time, PrivacyHasher hasher, TenantLifecycleSettings? settings = null, ITenantDeletionSignal? signal = null)
    {
        _unitOfWork = unitOfWork;
        _time = time;
        _hasher = hasher;
        _settings = settings ?? TenantLifecycleSettings.Default;
        _signal = signal;
    }

    public TenantLifecycleSettings Settings => _settings;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public Task<TenantLifecycleOutcome> SuspendForBillingAsync(Guid tenantId, string reasonCode, CancellationToken cancellationToken = default)
        => ChangeAsSystemAsync(tenantId, reasonCode, (tenant, code, now) => tenant.SuspendForBilling(code, now), cancellationToken);

    public Task<TenantLifecycleOutcome> ResumeFromBillingAsync(Guid tenantId, string reasonCode, CancellationToken cancellationToken = default)
        => ChangeAsSystemAsync(tenantId, reasonCode, (tenant, code, now) => tenant.ResumeFromBilling(code, now), cancellationToken);

    public async Task<TenantLifecycleOutcome> CancelForNonPaymentAsync(Guid tenantId, string reasonCode, CancellationToken cancellationToken = default)
    {
        var code = ReasonCodes.Required(reasonCode);
        await using var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException();
        var status = change.Tenant.Status;
        if (status == TenantStatus.Cancelled)
        {
            return TenantLifecycleOutcome.Unchanged;
        }

        if (status == TenantStatus.Pending)
        {
            return TenantLifecycleOutcome.NotApplicable;
        }

        await CancelLockedAsync(change, null, null, code, cancellationToken);
        await change.CommitAsync(cancellationToken);
        return TenantLifecycleOutcome.Changed;
    }

    /// <summary>
    /// Cancels the locked company for <paramref name="actorId"/> (null: the system, with <paramref name="reasonCode"/>): switches its lines off,
    /// releases its licences (each release audited) and audits the cancellation with the deletion date. The caller commits.
    /// </summary>
    public async Task CancelLockedAsync(ITenantChange change, Guid? actorId, string? reason, string? reasonCode, CancellationToken cancellationToken)
    {
        var tenant = change.Tenant;
        var from = tenant.Status;
        var now = Now;
        tenant.Cancel(actorId, reason, reasonCode, now, _settings.DeletionRetentionDays);

        var linesOff = 0;
        foreach (var line in (await change.GetChannelsAsync(cancellationToken)).Where(c => c.IsActive))
        {
            line.Deactivate();
            linesOff++;
        }

        var released = 0;
        foreach (var license in await change.GetAssignedLicensesAsync(cancellationToken))
        {
            var channelId = license.ChannelId;
            license.Release(now);
            released++;
            change.Record(Audit(actorId, reasonCode, PlatformAuditLog.Actions.LicenseReleased, tenant.Id, new Dictionary<string, object?>
            {
                ["licenseId"] = license.Id,
                ["number"] = license.MaskedPhoneNumber,
                ["channelId"] = channelId,
                ["lineDeactivated"] = channelId is not null,
            }));
        }

        change.Record(Audit(actorId, reasonCode, PlatformAuditLog.Actions.TenantStatusChanged, tenant.Id, new Dictionary<string, object?>
        {
            ["from"] = from.ToString(),
            ["to"] = TenantStatus.Cancelled.ToString(),
            ["reason"] = tenant.StatusReason,
            ["deletionDueAt"] = tenant.DataDeletionDueAt,
            ["linesDeactivated"] = linesOff,
            ["licensesReleased"] = released,
        }));
    }

    public async Task<TenantLifecycleOutcome> BeginScheduledDeletionAsync(Guid tenantId, DateTime asOf, CancellationToken cancellationToken = default)
    {
        await using var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException();
        var tenant = change.Tenant;
        if (!tenant.IsDataDeletionDue(asOf))
        {
            return TenantLifecycleOutcome.NotApplicable;
        }

        if (!await BeginDeletionLockedAsync(change, null, null, TenantDeletionTrigger.Scheduled, ReasonCodes.DeletionRetentionExpired, cancellationToken))
        {
            return TenantLifecycleOutcome.Unchanged;
        }

        await change.CommitAsync(cancellationToken);
        return TenantLifecycleOutcome.Changed;
    }

    /// <summary>
    /// Starts the deletion of the locked, cancelled company: from now on it cannot be reactivated. Writes the tombstone and audits it; a
    /// licence still assigned to it is released. False, and nothing queued, when it had already started. The caller commits, then
    /// <see cref="NotifyDeletionJob"/>.
    /// </summary>
    public async Task<bool> BeginDeletionLockedAsync(
        ITenantChange change, Guid? actorId, string? reason, TenantDeletionTrigger trigger, string reasonCode, CancellationToken cancellationToken)
    {
        var tenant = change.Tenant;
        var now = Now;
        if (tenant.Status != TenantStatus.Cancelled)
        {
            throw new TenantLifecycleConflictException(TenantLifecycleConflictException.NotCancelled);
        }

        if (!tenant.BeginDataDeletion(now))
        {
            return false;
        }

        foreach (var license in await change.GetAssignedLicensesAsync(cancellationToken))
        {
            license.Release(now);
        }

        var name = _hasher.TenantName(tenant.Name);
        change.AddDeletionRecord(TenantDeletion.Start(tenant, name.Hex, name.KeyId, trigger, actorId, now));
        change.Record(Audit(actorId, reasonCode, PlatformAuditLog.Actions.TenantDataDeletionStarted, tenant.Id, new Dictionary<string, object?>
        {
            ["trigger"] = trigger.ToString(),
            ["reason"] = reason,
            ["cancelledAt"] = tenant.CancelledAt,
            ["deletionDueAt"] = tenant.DataDeletionDueAt,
        }));
        return true;
    }

    public void NotifyDeletionJob() => _signal?.Notify();

    private async Task<TenantLifecycleOutcome> ChangeAsSystemAsync(
        Guid tenantId, string reasonCode, Func<Tenant, string, DateTime, TenantLifecycleOutcome> move, CancellationToken cancellationToken)
    {
        var code = ReasonCodes.Required(reasonCode);
        await using var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException();
        var tenant = change.Tenant;
        var from = tenant.Status;
        var outcome = move(tenant, code, Now);
        if (outcome != TenantLifecycleOutcome.Changed)
        {
            return outcome;
        }

        change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.TenantStatusChanged, tenantId, code, new Dictionary<string, object?>
        {
            ["from"] = from.ToString(),
            ["to"] = tenant.Status.ToString(),
            ["cause"] = tenant.SuspensionCause?.ToString(),
            // A paused company stays paused: the late payment is a hold that the resume of the pause honours.
            ["billingHold"] = tenant.Status == TenantStatus.Paused ? tenant.BillingHoldSince is not null : null,
        }));
        await change.CommitAsync(cancellationToken);
        return outcome;
    }

    private static PlatformAuditLog Audit(Guid? actorId, string? reasonCode, string action, Guid tenantId, IReadOnlyDictionary<string, object?> details)
        => actorId is { } admin
            ? PlatformAuditLog.Record(admin, action, tenantId, details, reasonCode)
            : PlatformAuditLog.RecordSystem(action, tenantId, reasonCode!, details);
}
