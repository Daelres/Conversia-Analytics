using Conversia.Domain.Entities;

namespace Conversia.Domain.ReadModels;

/// <summary>What the billing engine needs of the company: its line quota and where its lifecycle stands.</summary>
public sealed record BillingTenantFacts(int MaxActiveLines, Enums.TenantStatus Status, Enums.SuspensionCause? SuspensionCause, DateTime? BillingHoldSince)
{
    /// <summary>Suspended for billing, or paused with a billing hold (docs/PLATFORM_ADMIN.md, "Lifecycle of a company").</summary>
    public bool InBillingSuspension
        => (Status == Enums.TenantStatus.Suspended && SuspensionCause == Enums.SuspensionCause.Billing)
           || (Status == Enums.TenantStatus.Paused && BillingHoldSince is not null);

    /// <summary>A late payment suspends it: Active, or Paused without a hold. Never a Pending, manually suspended or cancelled company.</summary>
    public bool CanBeSuspendedForBilling
        => Status == Enums.TenantStatus.Active || (Status == Enums.TenantStatus.Paused && BillingHoldSince is null);
}

/// <summary>A company's subscription as the billing pages show it (untracked rows).</summary>
/// <param name="Charges">Newest first.</param>
/// <param name="Attempts">The attempts of <paramref name="Charges"/>, newest first.</param>
public sealed record BillingAccountView(
    BillingSubscription Subscription,
    BillingPlan Plan,
    BillingAddOn? AddOn,
    int TenantMaxActiveLines,
    IReadOnlyList<BillingCharge> Charges,
    IReadOnlyList<BillingPaymentAttempt> Attempts);
