using Conversia.Domain.Enums;

namespace Conversia.Application.Billing;

/// <summary>
/// The dunning and job settings (<c>Billing:*</c>, docs/BILLING.md, "Configuration"). Validated here, so a bad value stops the API at startup.
/// </summary>
public sealed record BillingSettings
{
    public static readonly IReadOnlyList<int> DefaultRetryDays = [1, 3, 5];
    public const int DefaultGraceDays = 7;
    public const int DefaultCancelAfterSuspendedDays = 30;
    public const int DefaultAttemptHourLocal = 9;
    public const int MaxDays = 90;

    public BillingSettings(
        IReadOnlyList<int>? retryDays = null,
        int graceDays = DefaultGraceDays,
        int cancelAfterSuspendedDays = DefaultCancelAfterSuspendedDays,
        int attemptHourLocal = DefaultAttemptHourLocal,
        TimeSpan? pendingPollInterval = null,
        TimeSpan? unknownAttemptTimeout = null,
        int paymentLinkValidDays = 3)
    {
        var retries = retryDays ?? DefaultRetryDays;
        if (retries.Count > 10 || retries.Any(d => d is < 1 or > MaxDays) || retries.Zip(retries.Skip(1)).Any(p => p.Second <= p.First))
        {
            throw new ArgumentException($"The retry days must be at most 10 increasing whole numbers between 1 and {MaxDays}.", nameof(retryDays));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(graceDays, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(graceDays, MaxDays);
        ArgumentOutOfRangeException.ThrowIfLessThan(cancelAfterSuspendedDays, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cancelAfterSuspendedDays, 365);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptHourLocal, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(attemptHourLocal, 23);
        ArgumentOutOfRangeException.ThrowIfLessThan(paymentLinkValidDays, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(paymentLinkValidDays, 30);

        RetryDays = retries.ToArray();
        GraceDays = graceDays;
        CancelAfterSuspendedDays = cancelAfterSuspendedDays;
        AttemptHourLocal = attemptHourLocal;
        PendingPollInterval = pendingPollInterval ?? TimeSpan.FromMinutes(2);
        UnknownAttemptTimeout = unknownAttemptTimeout ?? TimeSpan.FromHours(24);
        PaymentLinkValidDays = paymentLinkValidDays;
    }

    public static BillingSettings Default { get; } = new();

    /// <summary>Days after the due date of each automatic retry (default 1, 3 and 5). The first attempt is on the due date.</summary>
    public IReadOnlyList<int> RetryDays { get; }

    /// <summary>Days after the due date the company is suspended for billing if still unpaid (default 7).</summary>
    public int GraceDays { get; }

    /// <summary>Days suspended for billing before the subscription and the company are cancelled for non-payment (default 30; 0 = never).</summary>
    public int CancelAfterSuspendedDays { get; }

    /// <summary>The local hour (America/Bogota) automatic attempts are made at, so a Nequi approval never wakes anyone up (default 9).</summary>
    public int AttemptHourLocal { get; }

    /// <summary>How often a pending transaction is asked about (the events usually answer first).</summary>
    public TimeSpan PendingPollInterval { get; }

    /// <summary>How long an attempt whose outcome is unknown (no transaction id) is looked up by reference before it counts as failed.</summary>
    public TimeSpan UnknownAttemptTimeout { get; }

    public int PaymentLinkValidDays { get; }
}

/// <summary>The dunning timeline (docs/BILLING.md, "Dunning timeline"), on local dates. Pure.</summary>
public static class DunningPolicy
{
    /// <summary>
    /// The local date of the next automatic attempt after <paramref name="startedAttempts"/> attempts: the due date for the first, then each
    /// retry day after the due date; null when none is left.
    /// </summary>
    public static DateOnly? NextAttemptDate(DateOnly dueOn, int startedAttempts, BillingSettings settings)
        => startedAttempts switch
        {
            < 0 => throw new ArgumentOutOfRangeException(nameof(startedAttempts)),
            0 => dueOn,
            _ when startedAttempts <= settings.RetryDays.Count => dueOn.AddDays(settings.RetryDays[startedAttempts - 1]),
            _ => null,
        };

    /// <summary>The local date the company is suspended for billing if the charge is still unpaid.</summary>
    public static DateOnly SuspensionDate(DateOnly dueOn, BillingSettings settings) => dueOn.AddDays(settings.GraceDays);

    /// <summary>The local date the subscription is cancelled for non-payment; null when it never is (setting 0).</summary>
    public static DateOnly? CancellationDate(DateOnly suspendedOn, BillingSettings settings)
        => settings.CancelAfterSuspendedDays == 0 ? null : suspendedOn.AddDays(settings.CancelAfterSuspendedDays);

    /// <summary>Where the collection stands, for the pages.</summary>
    public static DunningStage Stage(SubscriptionStatus status, bool hasOverdueCharge, bool suspendedForBilling)
        => status == SubscriptionStatus.Cancelled ? DunningStage.Cancelled
            : suspendedForBilling ? DunningStage.Suspended
            : hasOverdueCharge ? DunningStage.Retrying
            : DunningStage.None;
}
