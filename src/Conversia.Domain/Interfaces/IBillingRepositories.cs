using Conversia.Domain.Entities;
using Conversia.Domain.ReadModels;

namespace Conversia.Domain.Interfaces;

/// <summary>
/// Starts a change to one company's subscription: locks its row (<c>SELECT ... FOR UPDATE</c>) in a transaction, so the billing job, the
/// gateway's events, the owner and the administrator never interleave on the same subscription (a period is charged once, a second attempt
/// never starts while one is pending). The company is always named explicitly: the job and the events endpoint run without one.
/// </summary>
public interface IBillingUnitOfWork
{
    /// <summary>
    /// Null when the company has no subscription, or, with <paramref name="skipIfLocked"/>, when another instance holds it (<c>SKIP LOCKED</c>):
    /// the job then leaves it to that instance. Dispose without committing to roll back.
    /// </summary>
    Task<IBillingChange?> BeginAsync(Guid tenantId, bool skipIfLocked = false, CancellationToken cancellationToken = default);
}

/// <summary>A locked subscription and what a change to it may read and write. Nothing is persisted until <see cref="CommitAsync"/>.</summary>
public interface IBillingChange : IAsyncDisposable
{
    BillingSubscription Subscription { get; }

    /// <summary>The company's line quota (what is billed when the administrator set no number) and status; null if it is gone.</summary>
    Task<BillingTenantFacts?> GetTenantFactsAsync(CancellationToken cancellationToken = default);

    Task<BillingPlan?> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default);

    Task<BillingAddOn?> GetAddOnAsync(Guid addOnId, CancellationToken cancellationToken = default);

    /// <summary>The oldest open charge of the subscription, tracked, as it stands in this change (a charge paid in it is no longer open).</summary>
    Task<BillingCharge?> GetOpenChargeAsync(CancellationToken cancellationToken = default);

    /// <summary>Every open charge of the subscription, tracked, oldest first, as they stand in this change.</summary>
    Task<IReadOnlyList<BillingCharge>> GetOpenChargesAsync(CancellationToken cancellationToken = default);

    /// <summary>A charge of THIS subscription, tracked; null when it does not exist or belongs to another one.</summary>
    Task<BillingCharge?> GetChargeAsync(Guid chargeId, CancellationToken cancellationToken = default);

    Task<bool> HasChargeForPeriodAsync(DateOnly periodStart, CancellationToken cancellationToken = default);

    /// <summary>The attempts of a charge of THIS subscription, tracked, oldest first.</summary>
    Task<IReadOnlyList<BillingPaymentAttempt>> GetAttemptsAsync(Guid chargeId, CancellationToken cancellationToken = default);

    Task<BillingPaymentAttempt?> GetAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default);

    /// <summary>Every pending attempt of THIS company, whatever its charge (a charge settled by other means may still have one), tracked.</summary>
    Task<IReadOnlyList<BillingPaymentAttempt>> GetPendingAttemptsAsync(CancellationToken cancellationToken = default);

    Task<BillingPaymentAttempt?> GetAttemptByTransactionAsync(string transactionId, CancellationToken cancellationToken = default);

    void Add(BillingCharge charge);

    void Add(BillingPaymentAttempt attempt);

    void Add(AiQuotaGrant grant);

    void Add(BillingReviewItem item);

    /// <summary>True when a review item about this transaction is still open (one per transaction).</summary>
    Task<bool> HasOpenReviewItemAsync(string transactionId, CancellationToken cancellationToken = default);

    void Record(PlatformAuditLog entry);

    /// <summary>Saves and commits. A row that changed since it was read throws <see cref="Exceptions.ConcurrencyConflictException"/>.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);
}

/// <summary>The catalogue of plans and the reads and lookups of the billing data that need no lock.</summary>
public interface IBillingRepository
{
    Task<IReadOnlyList<BillingPlan>> ListPlansAsync(CancellationToken cancellationToken = default);

    /// <summary>Tracked.</summary>
    Task<BillingPlan?> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BillingAddOn>> ListAddOnsAsync(CancellationToken cancellationToken = default);

    /// <summary>Tracked.</summary>
    Task<BillingAddOn?> GetAddOnAsync(Guid addOnId, CancellationToken cancellationToken = default);

    /// <summary>Adds the plan with its audit row. False when the code is taken.</summary>
    Task<bool> TryAddPlanAsync(BillingPlan plan, PlatformAuditLog audit, CancellationToken cancellationToken = default);

    /// <summary>Adds the add-on with its audit row. False when the plan already has one with that code.</summary>
    Task<bool> TryAddAddOnAsync(BillingAddOn addOn, PlatformAuditLog audit, CancellationToken cancellationToken = default);

    /// <summary>Saves the tracked changes of the catalogue with their audit row.</summary>
    Task SaveCatalogueAsync(PlatformAuditLog audit, CancellationToken cancellationToken = default);

    /// <summary>Adds the subscription with its audit row. False when the company already has one.</summary>
    Task<bool> TryAddSubscriptionAsync(BillingSubscription subscription, PlatformAuditLog audit, CancellationToken cancellationToken = default);

    /// <summary>The subscription of the company with its plan, charges (newest first, at most <paramref name="maxCharges"/>) and their attempts.</summary>
    Task<BillingAccountView?> GetAccountAsync(Guid tenantId, int maxCharges, CancellationToken cancellationToken = default);

    /// <summary>The companies whose subscription the job must look at (<c>NextRunAt &lt;= now</c>), most overdue first.</summary>
    Task<IReadOnlyList<Guid>> ListDueAsync(DateTime now, int max, CancellationToken cancellationToken = default);

    /// <summary>The company and attempt our reference belongs to.</summary>
    Task<(Guid TenantId, Guid AttemptId)?> FindAttemptByReferenceAsync(string reference, CancellationToken cancellationToken = default);

    Task<(Guid TenantId, Guid AttemptId)?> FindAttemptByTransactionAsync(string transactionId, CancellationToken cancellationToken = default);

    /// <summary>The company and charge a payment link belongs to.</summary>
    Task<(Guid TenantId, Guid ChargeId)?> FindChargeByPaymentLinkAsync(string paymentLinkId, CancellationToken cancellationToken = default);

    /// <summary>Stores a review item outside any subscription (an approved payment that matches nothing of ours).</summary>
    Task AddReviewItemAsync(BillingReviewItem item, CancellationToken cancellationToken = default);

    /// <summary>The review items not yet reviewed, oldest first (optionally of one company).</summary>
    Task<IReadOnlyList<BillingReviewItem>> ListOpenReviewItemsAsync(Guid? tenantId, int max, CancellationToken cancellationToken = default);

    /// <summary>Tracked, for the administrator's verdict; saved with its audit row by <see cref="SaveCatalogueAsync"/>.</summary>
    Task<BillingReviewItem?> GetReviewItemAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Companies suspended for billing (or paused with a billing hold) whose latest subscription is cancelled: nothing can be owed any more,
    /// so the job lifts them (self-healing; docs/BILLING.md, "The dunning step").
    /// </summary>
    Task<IReadOnlyList<Guid>> ListOrphanBillingSuspensionsAsync(int max, CancellationToken cancellationToken = default);

    /// <summary>Stores a verified gateway event. False when the same body was stored already (a re-sent event).</summary>
    Task<bool> TryAddEventAsync(BillingEvent billingEvent, CancellationToken cancellationToken = default);
}
