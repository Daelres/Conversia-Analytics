using Conversia.Domain.Enums;

namespace Conversia.Application.Interfaces;

/// <summary>
/// The lifecycle changes a background job makes (docs/PLATFORM_ADMIN.md, "Lifecycle of a company"): the hooks the recurring charge (unit 1c)
/// calls, and the start of a scheduled deletion. Every call is idempotent, is audited as the system with its reason code, and throws
/// <see cref="Exceptions.TenantNotFoundException"/> for an unknown company and a <see cref="Domain.Exceptions.DomainException"/> for a reason
/// code that is not one (free text is refused).
/// </summary>
public interface ITenantBillingLifecycle
{
    /// <summary>
    /// An Active company with a late payment: Suspended (cause Billing). Its messages keep being received; its users see "payment pending". A
    /// Paused company stays paused with a billing hold: resuming the pause then lands in the billing suspension.
    /// </summary>
    Task<TenantLifecycleOutcome> SuspendForBillingAsync(Guid tenantId, string reasonCode, CancellationToken cancellationToken = default);

    /// <summary>Lifts a billing suspension, or the hold of a paused company (never an administrator's suspension, never a cancellation).</summary>
    Task<TenantLifecycleOutcome> ResumeFromBillingAsync(Guid tenantId, string reasonCode, CancellationToken cancellationToken = default);

    /// <summary>Cancels the company like an administrator would (lines off, licences released, deletion scheduled). Pending companies are left alone.</summary>
    Task<TenantLifecycleOutcome> CancelForNonPaymentAsync(Guid tenantId, string reasonCode, CancellationToken cancellationToken = default);
}

/// <summary>The start of the deletion of a cancelled company's data by the background job, once its retention window passed.</summary>
public interface ITenantDeletionScheduler
{
    /// <summary>
    /// <see cref="TenantLifecycleOutcome.Changed"/> when it started now (tombstone written, audited), <see cref="TenantLifecycleOutcome.Unchanged"/>
    /// when it had started already (resume it), <see cref="TenantLifecycleOutcome.NotApplicable"/> when the company is not cancelled or not due.
    /// </summary>
    /// <param name="asOf">The job's "now": due from the exact instant the window ends (<c>DataDeletionDueAt &lt;= asOf</c>).</param>
    Task<TenantLifecycleOutcome> BeginScheduledDeletionAsync(Guid tenantId, DateTime asOf, CancellationToken cancellationToken = default);
}

/// <summary>Wakes the deletion job of this instance, so a deletion the administrator asked for starts at once instead of at the next run.</summary>
public interface ITenantDeletionSignal
{
    void Notify();
}
