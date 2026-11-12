namespace Conversia.Domain.Enums;

// The statuses of the recurring charge (docs/BILLING.md). Kept together because they only make sense side by side. Stored by name.

/// <summary>Where a company's subscription stands (docs/BILLING.md, "Subscription states").</summary>
public enum SubscriptionStatus
{
    /// <summary>A free period the administrator gave: nothing is charged until it ends.</summary>
    Trialing = 0,

    /// <summary>Paid up: the charge of the current period is paid (or not due yet).</summary>
    Active = 1,

    /// <summary>The charge of the current period is due and unpaid: retries, then the billing suspension of the company.</summary>
    PastDue = 2,

    /// <summary>Ended: nothing is charged any more.</summary>
    Cancelled = 3,
}

/// <summary>A charge of one period (not a tax invoice: electronic invoicing is out of scope).</summary>
public enum BillingChargeStatus
{
    /// <summary>Waiting for the payment (including while it is retried).</summary>
    Open = 0,

    Paid = 1,

    /// <summary>Closed unpaid: the subscription ended for non-payment. The administrator can still mark it paid.</summary>
    Failed = 2,

    /// <summary>Annulled by the administrator, or by an immediate cancellation: nothing is owed.</summary>
    Void = 3,
}

/// <summary>One try to collect a charge through the gateway. Mirrors the gateway's statuses.</summary>
public enum PaymentAttemptStatus
{
    /// <summary>Sent, no final answer yet (a Nequi push waiting for the payer, a timeout whose outcome is unknown).</summary>
    Pending = 0,

    Approved = 1,

    Declined = 2,

    Voided = 3,

    Error = 4,
}

/// <summary>Who started an attempt.</summary>
public enum PaymentAttemptKind
{
    /// <summary>The billing job, on the due date and on each retry day.</summary>
    Automatic = 0,

    /// <summary>The company's owner pressed "Pagar ahora".</summary>
    OwnerInitiated = 1,

    /// <summary>A payment made through the charge's payment link (PSE and the other manual methods).</summary>
    PaymentLink = 2,
}

/// <summary>The kind of stored payment method.</summary>
public enum PaymentMethodType
{
    Card = 0,

    Nequi = 1,
}

/// <summary>How a paid charge was settled.</summary>
public enum ChargeSettlement
{
    /// <summary>An approved gateway transaction.</summary>
    Gateway = 0,

    /// <summary>The administrator marked it paid (a bank transfer, cash...), with the reason.</summary>
    Manual = 1,

    /// <summary>Nothing to collect (a plan at price 0).</summary>
    Free = 2,
}

/// <summary>A gateway event stored by the events endpoint and processed by the billing job.</summary>
public enum BillingEventStatus
{
    Pending = 0,

    Processed = 1,

    /// <summary>Gave up after the maximum attempts (the transaction could not be read).</summary>
    Failed = 2,

    /// <summary>Verified but about nothing of ours (another integration of the same merchant, an unknown reference).</summary>
    Ignored = 3,
}

/// <summary>Where a company is in the collection of an unpaid charge (docs/BILLING.md, "Dunning timeline"). Derived, never stored.</summary>
public enum DunningStage
{
    /// <summary>Nothing overdue.</summary>
    None = 0,

    /// <summary>A charge is overdue and is being retried; the company still works.</summary>
    Retrying = 1,

    /// <summary>The grace period passed: the company is suspended for billing until it pays.</summary>
    Suspended = 2,

    /// <summary>The subscription ended for non-payment.</summary>
    Cancelled = 3,
}
