using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Time;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.Billing;

/// <summary>
/// Every change of money state of a subscription (docs/BILLING.md, "How it works"): the job's run (new period and its charge, automatic
/// attempts, polling, dunning), the application of a gateway answer (from a poll or an event), and the owner's and the administrator's actions.
/// <list type="bullet">
/// <item>Every decision is taken under the lock of the subscription row (<see cref="IBillingUnitOfWork"/>); the gateway is called with no
/// lock and no transaction open, and its answer is applied under the lock again. An attempt is created (and committed) before the gateway is
/// called, and a new one never starts while another of the same charge is pending: a period is never charged twice.</item>
/// <item>An answer is applied only when the transaction's amount, currency and reference match the attempt; it is always one the gateway
/// gave us (a poll or a re-read after an event), never the content of an event.</item>
/// <item>The company's lifecycle changes (billing suspension, resume, cancellation) go through <see cref="ITenantBillingLifecycle"/> after
/// the billing change is committed; each is idempotent and is retried by the next run until the subscription records it.</item>
/// <item>Every money state change is audited: the system with a reason code, or the administrator.</item>
/// </list>
/// </summary>
public sealed class BillingEngine
{
    /// <summary>The event of an approved payment that could not be applied: a person must look at it (a refund, a charge to settle by hand).</summary>
    public static readonly EventId PaymentNeedsReview = new(4301, "BillingPaymentNeedsReview");

    /// <summary>The event of a lifecycle change of the company that failed (retried by the next run).</summary>
    public static readonly EventId LifecycleChangeFailed = new(4302, "BillingLifecycleChangeFailed");

    /// <summary>A period that ended more than this many days ago (the company was past due) restarts on the day it is paid.</summary>
    public const int LateRestartDays = 3;

    /// <summary>The least time between two automatic attempts of a charge, whatever the retry calendar says (the job may have been down).</summary>
    public static readonly TimeSpan MinRetrySpacing = TimeSpan.FromHours(12);

    /// <summary>An attempt without a transaction id is first looked up by reference after this long.</summary>
    public static readonly TimeSpan LookupDelay = TimeSpan.FromMinutes(2);

    private readonly IBillingUnitOfWork _unitOfWork;
    private readonly IPaymentGateway _gateway;
    private readonly ITenantBillingLifecycle _lifecycle;
    private readonly TimeProvider _time;
    private readonly TenantTimeZone _zone;
    private readonly BillingSettings _settings;
    private readonly ILogger<BillingEngine> _logger;

    private readonly IServiceScopeFactory? _scopes;

    /// <param name="scopes">
    /// When given (the API), the lifecycle hooks run in a scope of their own (their own connection) while this engine holds the subscription's
    /// lock, so the decision and the lifecycle change are one step. Lock order: subscription row, then company row; nothing takes them the
    /// other way round (docs/BILLING.md, "The dunning step").
    /// </param>
    public BillingEngine(
        IBillingUnitOfWork unitOfWork, IPaymentGateway gateway, ITenantBillingLifecycle lifecycle, TimeProvider time, TenantTimeZone zone,
        BillingSettings settings, ILogger<BillingEngine> logger, IServiceScopeFactory? scopes = null)
    {
        _scopes = scopes;
        _unitOfWork = unitOfWork;
        _gateway = gateway;
        _lifecycle = lifecycle;
        _time = time;
        _zone = zone;
        _settings = settings;
        _logger = logger;
    }

    private DateTime Now => DateTime.SpecifyKind(_time.GetUtcNow().UtcDateTime, DateTimeKind.Utc);

    private DateOnly Today => _zone.LocalDate(Now);

    /// <summary>The instant of an automatic attempt on a local date: the configured local hour.</summary>
    public DateTime AttemptTime(DateOnly date) => _zone.StartOfDayUtc(date).AddHours(_settings.AttemptHourLocal);

    // ------------------------------------------------------------------ the job

    /// <summary>One run of the job for one company. Skips it when another instance holds its subscription.</summary>
    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var work = await DecideAsync(tenantId, cancellationToken);
        if (work is null)
        {
            return;
        }

        foreach (var charge in work.Charges)
        {
            await ExecuteAttemptAsync(tenantId, charge.AttemptId, charge.Request, cancellationToken);
        }

        foreach (var check in work.Checks)
        {
            await CheckPendingAsync(tenantId, check, cancellationToken);
        }

        // Decided again now, under the lock, from what the gateway answered meanwhile: a payment approved in this very run is never followed
        // by a suspension or a cancellation.
        await ApplyDunningAsync(tenantId, cancellationToken);
    }

    private sealed class Work
    {
        public List<(Guid AttemptId, GatewayChargeRequest Request)> Charges { get; } = [];

        public List<PendingCheck> Checks { get; } = [];
    }

    private sealed record PendingCheck(Guid AttemptId, string? TransactionId, string Reference, DateTime AttemptedAt);

    private async Task<Work?> DecideAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var change = await _unitOfWork.BeginAsync(tenantId, skipIfLocked: true, cancellationToken);
        if (change is null)
        {
            return null;
        }

        var subscription = change.Subscription;
        if (await change.GetTenantFactsAsync(cancellationToken) is not { } tenant)
        {
            return null;
        }

        if (subscription.IsCancelled)
        {
            // Nothing to charge, but its pending attempts are still asked about, and the dunning step still runs (a company left suspended
            // for billing is lifted).
            var cancelledWork = await PollOnlyAsync(change, cancellationToken);
            await change.CommitAsync(cancellationToken);
            return cancelledWork;
        }

        var now = Now;
        var today = Today;
        var work = new Work();
        var open = await change.GetOpenChargeAsync(cancellationToken);

        if (tenant.Status == TenantStatus.Cancelled)
        {
            // The company was cancelled (by the administrator: only its row changed). Billing follows on its own side, on this run: the
            // subscription ends, what was open is voided, nothing is ever charged again; a payment still pending is asked about until it ends.
            foreach (var unpaid in await change.GetOpenChargesAsync(cancellationToken))
            {
                unpaid.Void("Empresa cancelada", now);
                change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingChargeVoided, tenantId, ReasonCodes.BillingSubscriptionEnded, ChargeDetails(unpaid)));
            }

            Cancel(change, null, ReasonCodes.BillingSubscriptionEnded, now);
            var afterCancel = await PollOnlyAsync(change, cancellationToken);
            await change.CommitAsync(cancellationToken);
            return afterCancel;
        }

        if (subscription.CancelAtPeriodEnd && today >= subscription.CurrentPeriodEnd)
        {
            // The end the owner or the administrator asked for: no new period; the dunning step cancels the company, then the subscription.
            subscription.Schedule(now + _settings.PendingPollInterval);
            await change.CommitAsync(cancellationToken);
            return work;
        }

        if (open is null && today >= subscription.CurrentPeriodEnd)
        {
            open = await StartNextPeriodAsync(change, tenant.MaxActiveLines, today, now, cancellationToken);
        }

        // The record of the suspension follows the company's real state: the administrator may have lifted it (the grace starts again
        // from today, never from a stale date) or the lifecycle may have moved without our record (adopted now).
        if (subscription.BillingSuspendedAt is not null && !tenant.InBillingSuspension)
        {
            if (open is not null)
            {
                subscription.RestartDunning(today);
            }
            else
            {
                subscription.ClearBillingSuspension();
            }
        }
        else if (tenant.InBillingSuspension && subscription.BillingSuspendedAt is null && open is not null)
        {
            subscription.RecordBillingSuspension(now);
        }

        if (open is null)
        {
            subscription.ClearDunningRestart();
        }

        // Every pending attempt of the company is asked about, whatever its charge: one of a charge paid or voided by other means may still
        // be approved, and that money must reach a person.
        var pending = (await change.GetPendingAttemptsAsync(cancellationToken)).ToList();

        // When this run may suspend or cancel, every pending attempt of the overdue charge is asked about first, whatever its backoff: the
        // answer may be the payment.
        var deciding = open is not null && Evaluate(subscription, open, tenant, today, pendingOnOpen: false).Kind is DunningKind.Suspend or DunningKind.Cancel;
        foreach (var attempt in pending.Where(a => NextCheckAt(a) <= now || (deciding && a.ChargeId == open!.Id)))
        {
            work.Checks.Add(new PendingCheck(attempt.Id, attempt.GatewayTransactionId, attempt.Reference, attempt.AttemptedAt));
        }

        if (open is not null)
        {
            if (today > open.DueOn)
            {
                subscription.MarkPastDue();
            }

            // Never a new attempt while the outcome of one of this charge is unknown or pending (a payment link's included).
            var cancelling = Evaluate(subscription, open, tenant, today, pendingOnOpen: false).Kind == DunningKind.Cancel;
            if (!cancelling && !pending.Any(a => a.ChargeId == open.Id) && subscription.PaymentSourceId is not null && _gateway.IsConfigured && open.NextAttemptAt <= now)
            {
                var attempt = BeginAttempt(change, open, PaymentAttemptKind.Automatic, now);
                work.Charges.Add((attempt.Id, ChargeRequest(subscription, attempt)));
                pending.Add(attempt);
            }
        }

        subscription.Schedule(NextRun(subscription, open, pending, tenant, now));
        await change.CommitAsync(cancellationToken);
        return work;
    }

    /// <summary>The work of a cancelled subscription: only the pending attempts that are due, and the next time to come back for them.</summary>
    private async Task<Work> PollOnlyAsync(IBillingChange change, CancellationToken cancellationToken)
    {
        var now = Now;
        var work = new Work();
        var pending = await change.GetPendingAttemptsAsync(cancellationToken);
        foreach (var attempt in pending.Where(a => NextCheckAt(a) <= now))
        {
            work.Checks.Add(new PendingCheck(attempt.Id, attempt.GatewayTransactionId, attempt.Reference, attempt.AttemptedAt));
        }

        change.Subscription.SchedulePoll(pending.Count == 0 ? null : pending.Select(NextCheckAt).Select(at => at > now ? at : now + _settings.PendingPollInterval).Min());
        return work;
    }

    /// <summary>When a pending attempt is asked about next: every poll interval when its transaction is known; with backoff when not (its outcome is unknown).</summary>
    private DateTime NextCheckAt(BillingPaymentAttempt attempt)
    {
        var since = attempt.LastCheckedAt ?? attempt.AttemptedAt;
        if (attempt.GatewayTransactionId is not null)
        {
            return since + _settings.PendingPollInterval;
        }

        var backoff = TimeSpan.FromTicks(LookupDelay.Ticks * (1L << Math.Min(attempt.Checks, 5)));
        return since + (backoff > MaxLookupInterval ? MaxLookupInterval : backoff);
    }

    /// <summary>The longest wait between two lookups of an attempt whose outcome is unknown.</summary>
    public static readonly TimeSpan MaxLookupInterval = TimeSpan.FromHours(1);

    private enum DunningKind
    {
        None,
        Suspend,
        Resume,
        Cancel,
    }

    private readonly record struct DunningDecision(DunningKind Kind, string? ReasonCode = null);

    /// <summary>The one rule of the dunning step, from the state as it is now (docs/BILLING.md, "Dunning timeline").</summary>
    /// <param name="pendingOnOpen">An attempt of the open charge is pending or of unknown outcome: nothing is suspended or cancelled until it ends.</param>
    private DunningDecision Evaluate(BillingSubscription subscription, BillingCharge? open, BillingTenantFacts tenant, DateOnly today, bool pendingOnOpen)
    {
        if (open is null || subscription.IsCancelled)
        {
            if (!subscription.IsCancelled && subscription.CancelAtPeriodEnd && today >= subscription.CurrentPeriodEnd)
            {
                return new(DunningKind.Cancel, ReasonCodes.BillingSubscriptionEnded);
            }

            // Nothing owed: a company still suspended for billing is lifted, whatever our record says (self-healing).
            return tenant.InBillingSuspension ? new(DunningKind.Resume) : new(DunningKind.None);
        }

        if (pendingOnOpen)
        {
            return new(DunningKind.None);
        }

        if (subscription.CancelAtPeriodEnd && today >= subscription.CurrentPeriodEnd)
        {
            return new(DunningKind.Cancel, ReasonCodes.BillingSubscriptionEnded);
        }

        if (tenant.InBillingSuspension)
        {
            return subscription.BillingSuspendedAt is { } at
                   && DunningPolicy.CancellationDate(_zone.LocalDate(at), _settings) is { } cancelOn && today >= cancelOn
                ? new(DunningKind.Cancel, ReasonCodes.BillingNonPayment)
                : new(DunningKind.None);
        }

        return tenant.CanBeSuspendedForBilling && today >= DunningPolicy.SuspensionDate(subscription.DunningFrom(open.DueOn), _settings)
            ? new(DunningKind.Suspend)
            : new(DunningKind.None);
    }

    private async Task<BillingCharge?> StartNextPeriodAsync(IBillingChange change, int maxActiveLines, DateOnly today, DateTime now, CancellationToken cancellationToken)
    {
        var subscription = change.Subscription;
        var start = subscription.CurrentPeriodEnd >= today.AddDays(-LateRestartDays) ? subscription.CurrentPeriodEnd : today;
        if (await change.HasChargeForPeriodAsync(start, cancellationToken))
        {
            // Cannot happen through the engine (the period moves in the same commit); never charge it twice anyway.
            subscription.StartPeriod(start);
            return null;
        }

        var plan = await change.GetPlanAsync(subscription.PlanId, cancellationToken) ?? throw new BillingException(BillingProblem.PlanNotFound);
        var addOn = subscription.AddOnId is { } addOnId ? await change.GetAddOnAsync(addOnId, cancellationToken) : null;
        subscription.StartPeriod(start);
        var charge = BillingCharge.Create(
            subscription, plan, subscription.BilledLines(maxActiveLines), addOn, subscription.AddOnQuantity, subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd, AttemptTime(start));
        change.Add(charge);
        change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingChargeCreated, subscription.TenantId, ReasonCodes.BillingPeriodStarted, ChargeDetails(charge)));

        if (charge.AmountInCents == 0)
        {
            charge.MarkPaid(ChargeSettlement.Free, null, now);
            change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingChargePaid, subscription.TenantId, ReasonCodes.BillingChargePaid, ChargeDetails(charge)));
            return null;
        }

        return charge;
    }

    private DateTime NextRun(BillingSubscription subscription, BillingCharge? open, IReadOnlyList<BillingPaymentAttempt> pending, BillingTenantFacts tenant, DateTime now)
    {
        var candidates = new List<DateTime> { _zone.StartOfDayUtc(subscription.CurrentPeriodEnd) };
        candidates.AddRange(pending.Select(NextCheckAt));
        if (open is not null)
        {
            if (!pending.Any(a => a.ChargeId == open.Id) && open.NextAttemptAt is { } next && subscription.PaymentSourceId is not null && _gateway.IsConfigured)
            {
                candidates.Add(next);
            }

            if (tenant.InBillingSuspension)
            {
                if (subscription.BillingSuspendedAt is { } suspendedAt && DunningPolicy.CancellationDate(_zone.LocalDate(suspendedAt), _settings) is { } cancelOn)
                {
                    candidates.Add(_zone.StartOfDayUtc(cancelOn));
                }
            }
            else if (tenant.CanBeSuspendedForBilling)
            {
                candidates.Add(_zone.StartOfDayUtc(DunningPolicy.SuspensionDate(subscription.DunningFrom(open.DueOn), _settings)));
            }

            candidates.Add(_zone.StartOfDayUtc(open.DueOn.AddDays(1))); // past due from the day after
        }

        // A state the rule cannot act on now (a manual suspension, a company not yet active) is looked at again within the hour.
        candidates.Add(now + TimeSpan.FromHours(1));
        // Only what is still ahead: whatever was due has been acted on in this run (or waits for its pending attempt).
        return candidates.Where(c => c > now).Min();
    }

    private BillingPaymentAttempt BeginAttempt(IBillingChange change, BillingCharge charge, PaymentAttemptKind kind, DateTime now, Guid? userId = null)
    {
        var subscription = change.Subscription;
        if (kind == PaymentAttemptKind.Automatic)
        {
            charge.BeginAutomaticAttempt();
        }

        var attempt = BillingPaymentAttempt.Begin(charge, kind, subscription.PaymentMethodType, now);
        change.Add(attempt);
        change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingPaymentAttempted, subscription.TenantId,
            kind == PaymentAttemptKind.OwnerInitiated ? ReasonCodes.BillingOwnerRequest : ReasonCodes.BillingAutomaticAttempt,
            new Dictionary<string, object?>
            {
                ["chargeId"] = charge.Id,
                ["attemptId"] = attempt.Id,
                ["kind"] = kind.ToString(),
                ["amountInCents"] = attempt.AmountInCents,
                ["method"] = subscription.PaymentMethodType?.ToString(),
                ["automaticAttempts"] = charge.AutomaticAttempts,
                ["userId"] = userId,
            }));
        return attempt;
    }

    private static GatewayChargeRequest ChargeRequest(BillingSubscription subscription, BillingPaymentAttempt attempt)
        => new(attempt.Reference, attempt.AmountInCents, attempt.Currency, subscription.BillingEmail!, subscription.PaymentSourceId!, subscription.PaymentMethodType!.Value);

    private async Task ExecuteAttemptAsync(Guid tenantId, Guid attemptId, GatewayChargeRequest request, CancellationToken cancellationToken)
    {
        // The token of the caller is not passed on: once the attempt is committed, its call must not be abandoned halfway by a shutdown.
        GatewayChargeResult result;
        try
        {
            result = await _gateway.ChargeAsync(request, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = GatewayChargeResult.Unknown(ex.GetType().Name);
        }

        switch (result.Outcome)
        {
            case GatewayChargeOutcome.Created:
                await ApplyAsync(tenantId, attemptId, result.Transaction!, cancellationToken);
                break;
            case GatewayChargeOutcome.Refused:
                await FailWithoutTransactionAsync(tenantId, attemptId, result.Reason ?? "refused", cancellationToken);
                break;
            default:
                // Unknown: the transaction may exist. The attempt stays pending and is looked up by its reference (and its event may arrive).
                _logger.LogWarning("The charge of attempt {AttemptId} of tenant {TenantId} has an unknown outcome ({Reason}); it will be looked up by reference.", attemptId, tenantId, result.Reason);
                break;
        }
    }

    private async Task CheckPendingAsync(Guid tenantId, PendingCheck check, CancellationToken cancellationToken)
    {
        GatewayTransaction? transaction;
        try
        {
            transaction = check.TransactionId is { } id
                ? await _gateway.GetTransactionAsync(id, cancellationToken)
                : await _gateway.FindTransactionByReferenceAsync(check.Reference, cancellationToken);
        }
        catch (BillingException ex)
        {
            _logger.LogWarning("Could not ask the gateway about attempt {AttemptId} of tenant {TenantId}: {Code}.", check.AttemptId, tenantId, ex.Code);
            transaction = null;
        }

        if (transaction is not null)
        {
            await ApplyAsync(tenantId, check.AttemptId, transaction, cancellationToken);
            return;
        }

        // No answer, or nothing found by our reference (a lookup the documentation does not show): the outcome stays unknown, the attempt
        // stays pending (so no second attempt can charge the same period) and is asked again with backoff. After the timeout a person is
        // told; only the administrator closes it, once checked in the gateway's dashboard.
        await using var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken: cancellationToken);
        if (change is not null && await change.GetAttemptAsync(check.AttemptId, cancellationToken) is { IsPending: true } attempt)
        {
            var now = Now;
            attempt.MarkChecked(now);
            if (attempt.AttemptedAt + _settings.UnknownAttemptTimeout <= now && attempt.FlagForReview(attempt.GatewayTransactionId is null ? "unknown_outcome" : "pending_too_long", now))
            {
                ReviewFlagged(change, attempt, null, attempt.ReviewReason!);
            }

            await change.CommitAsync(cancellationToken);
        }
    }

    // ------------------------------------------------------------------ answers of the gateway

    /// <summary>
    /// Applies a transaction the gateway reported (by a poll, or re-read after one of its events) to the attempt it belongs to:
    /// <paramref name="attemptId"/>, or the attempt that has its id, or, for a payment through a charge's link (<paramref name="paymentLinkChargeId"/>),
    /// a new attempt of that charge. Idempotent: an attempt resolves once.
    /// </summary>
    public async Task<bool> ApplyAsync(Guid tenantId, Guid? attemptId, GatewayTransaction transaction, CancellationToken cancellationToken = default, Guid? paymentLinkChargeId = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        bool resume;
        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken: cancellationToken))
        {
            if (change is null)
            {
                return false;
            }

            var now = Now;
            var attempt = attemptId is { } id
                ? await change.GetAttemptAsync(id, cancellationToken)
                : await change.GetAttemptByTransactionAsync(transaction.Id, cancellationToken);
            if (attempt is null && paymentLinkChargeId is { } chargeId && await change.GetChargeAsync(chargeId, cancellationToken) is { } linked
                && linked.PaymentLinkId is not null && transaction.PaymentLinkId == linked.PaymentLinkId)
            {
                attempt = BillingPaymentAttempt.FromPaymentLink(linked, transaction.Id, transaction.Reference, now);
                change.Add(attempt);
                change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingPaymentAttempted, tenantId, ReasonCodes.BillingOwnerRequest, new Dictionary<string, object?>
                {
                    ["chargeId"] = linked.Id,
                    ["attemptId"] = attempt.Id,
                    ["kind"] = PaymentAttemptKind.PaymentLink.ToString(),
                    ["transactionId"] = transaction.Id,
                    ["amountInCents"] = transaction.AmountInCents,
                }));
            }

            if (attempt is null)
            {
                return false;
            }

            resume = await SettleAsync(change, attempt, transaction, now, cancellationToken);
            await change.CommitAsync(cancellationToken);
        }

        if (resume)
        {
            await ApplyDunningAsync(tenantId, cancellationToken);
        }

        return true;
    }

    /// <returns>True when the charge got paid and the company is suspended for billing (resume it after the commit).</returns>
    private async Task<bool> SettleAsync(IBillingChange change, BillingPaymentAttempt attempt, GatewayTransaction transaction, DateTime now, CancellationToken cancellationToken)
    {
        var subscription = change.Subscription;
        var tenantId = subscription.TenantId;
        if (!attempt.IsPending)
        {
            if (attempt.GatewayTransactionId is { } own && own != transaction.Id)
            {
                return false;
            }

            if (attempt.Status == PaymentAttemptStatus.Approved && transaction.Status == PaymentAttemptStatus.Voided)
            {
                NeedsReview(change, attempt, transaction, "voided_after_approval");
            }
            else if (attempt.Status != PaymentAttemptStatus.Approved && transaction.Status == PaymentAttemptStatus.Approved)
            {
                // Money arrived for an attempt we had closed (an error, an administrator's verdict): never ignored.
                NeedsReview(change, attempt, transaction, "approved_after_resolution");
            }

            return false;
        }

        if (attempt.GatewayTransactionId is { } known && known != transaction.Id)
        {
            _logger.LogWarning("Transaction {TransactionId} does not belong to attempt {AttemptId} of tenant {TenantId}; ignored.", transaction.Id, attempt.Id, tenantId);
            return false;
        }

        var charge = await change.GetChargeAsync(attempt.ChargeId, cancellationToken)
                     ?? throw new InvalidOperationException("The charge of an attempt is gone.");
        var referenceMatches = attempt.Kind == PaymentAttemptKind.PaymentLink
            ? charge.PaymentLinkId is not null && transaction.PaymentLinkId == charge.PaymentLinkId
            : transaction.Reference == attempt.Reference;
        if (!referenceMatches)
        {
            _logger.LogWarning("Transaction {TransactionId} has another reference than attempt {AttemptId} of tenant {TenantId}; not applied.", transaction.Id, attempt.Id, tenantId);
            if (transaction.Status == PaymentAttemptStatus.Approved)
            {
                await ReviewItemAsync(change, BillingReviewItem.Flag("reference_mismatch", now, tenantId, charge.Id, attempt.Id, transaction.Id, transaction.AmountInCents, transaction.Currency), cancellationToken);
            }

            return false;
        }

        attempt.AttachTransaction(transaction.Id);
        if (transaction.Status == PaymentAttemptStatus.Pending)
        {
            attempt.MarkChecked(now);
            return false;
        }

        // Never trust an amount we did not ask for: an approval for another amount or currency pays nothing and goes to a person.
        if (transaction.AmountInCents != attempt.AmountInCents || !string.Equals(transaction.Currency, attempt.Currency, StringComparison.Ordinal))
        {
            attempt.Resolve(PaymentAttemptStatus.Error, "amount_mismatch", now);
            if (transaction.Status == PaymentAttemptStatus.Approved)
            {
                NeedsReview(change, attempt, transaction, "amount_mismatch");
            }

            AuditResolution(change, attempt, charge);
            OnFailure(change, charge, attempt, now);
            return false;
        }

        attempt.Resolve(transaction.Status, transaction.StatusMessage, now);
        AuditResolution(change, attempt, charge);
        if (transaction.Status != PaymentAttemptStatus.Approved)
        {
            OnFailure(change, charge, attempt, now);
            return false;
        }

        if (charge.Status is BillingChargeStatus.Paid or BillingChargeStatus.Void)
        {
            // Paid twice (a link and the card, a manual mark and a late approval) or paid after it was voided: refund by hand in the gateway.
            NeedsReview(change, attempt, transaction, charge.Status == BillingChargeStatus.Paid ? "duplicate_payment" : "paid_after_void");
            return false;
        }

        return MarkPaid(change, charge, ChargeSettlement.Gateway, null, null, null, now);
    }

    private void AuditResolution(IBillingChange change, BillingPaymentAttempt attempt, BillingCharge charge)
        => change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingPaymentResolved, attempt.TenantId,
            attempt.Status == PaymentAttemptStatus.Approved ? ReasonCodes.BillingChargePaid : ReasonCodes.BillingPaymentFailed,
            new Dictionary<string, object?>
            {
                ["chargeId"] = charge.Id,
                ["attemptId"] = attempt.Id,
                ["transactionId"] = attempt.GatewayTransactionId,
                ["status"] = attempt.Status.ToString(),
                ["reason"] = attempt.StatusReason,
                ["amountInCents"] = attempt.AmountInCents,
            }));

    /// <summary>Records a durable review item once per transaction (a replay never piles them up), with its audit row and log line.</summary>
    private async Task ReviewItemAsync(IBillingChange change, BillingReviewItem item, CancellationToken cancellationToken)
    {
        if (item.TransactionId is { } transactionId && await change.HasOpenReviewItemAsync(transactionId, cancellationToken))
        {
            return;
        }

        change.Add(item);
        _logger.LogError(PaymentNeedsReview, "Review item {ReviewItemId} ({Why}) for tenant {TenantId}, charge {ChargeId}, transaction {TransactionId}.", item.Id, item.Reason, item.TenantId, item.ChargeId, item.TransactionId);
        change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingPaymentNeedsReview, item.TenantId, ReasonCodes.BillingPaymentNeedsReview, new Dictionary<string, object?>
        {
            ["reviewItemId"] = item.Id,
            ["chargeId"] = item.ChargeId,
            ["attemptId"] = item.AttemptId,
            ["transactionId"] = item.TransactionId,
            ["amountInCents"] = item.AmountInCents,
            ["why"] = item.Reason,
        }));
    }

    private void NeedsReview(IBillingChange change, BillingPaymentAttempt attempt, GatewayTransaction transaction, string why)
    {
        if (!attempt.FlagForReview(why, Now))
        {
            return; // already waiting for a person: a replayed answer does not pile flags up
        }

        ReviewFlagged(change, attempt, transaction, why);
    }

    private void ReviewFlagged(IBillingChange change, BillingPaymentAttempt attempt, GatewayTransaction? transaction, string why)
    {
        if (transaction is null)
        {
            _logger.LogError(PaymentNeedsReview, "Attempt {AttemptId} of tenant {TenantId} needs a person: {Why}.", attempt.Id, attempt.TenantId, why);
            change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingPaymentNeedsReview, attempt.TenantId, ReasonCodes.BillingPaymentNeedsReview, new Dictionary<string, object?>
            {
                ["chargeId"] = attempt.ChargeId,
                ["attemptId"] = attempt.Id,
                ["reference"] = attempt.Reference,
                ["why"] = why,
            }));
            return;
        }

        _logger.LogError(
            PaymentNeedsReview,
            "Transaction {TransactionId} ({Status}, {AmountInCents} {Currency}) of attempt {AttemptId} of tenant {TenantId} needs a person: {Why}.",
            transaction.Id, transaction.Status, transaction.AmountInCents, transaction.Currency, attempt.Id, attempt.TenantId, why);
        change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingPaymentNeedsReview, attempt.TenantId, ReasonCodes.BillingPaymentNeedsReview, new Dictionary<string, object?>
        {
            ["chargeId"] = attempt.ChargeId,
            ["attemptId"] = attempt.Id,
            ["transactionId"] = transaction.Id,
            ["status"] = transaction.Status.ToString(),
            ["amountInCents"] = transaction.AmountInCents,
            ["currency"] = transaction.Currency,
            ["why"] = why,
        }));
    }

    private void OnFailure(IBillingChange change, BillingCharge charge, BillingPaymentAttempt attempt, DateTime now)
    {
        if (!charge.IsOpen)
        {
            return;
        }

        if (Today >= charge.DueOn)
        {
            change.Subscription.MarkPastDue();
        }

        if (attempt.Kind == PaymentAttemptKind.Automatic)
        {
            var next = DunningPolicy.NextAttemptDate(charge.DueOn, charge.AutomaticAttempts, _settings);
            charge.ScheduleNextAttempt(next is { } date ? Max(AttemptTime(date), now + MinRetrySpacing) : null);
        }

        change.Subscription.Schedule(now);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private async Task FailWithoutTransactionAsync(Guid tenantId, Guid attemptId, string reason, CancellationToken cancellationToken)
    {
        await using var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken: cancellationToken);
        if (change is null || await change.GetAttemptAsync(attemptId, cancellationToken) is not { IsPending: true } attempt)
        {
            return;
        }

        var now = Now;
        var charge = await change.GetChargeAsync(attempt.ChargeId, cancellationToken) ?? throw new InvalidOperationException("The charge of an attempt is gone.");
        attempt.Resolve(PaymentAttemptStatus.Error, reason, now);
        AuditResolution(change, attempt, charge);
        OnFailure(change, charge, attempt, now);
        await change.CommitAsync(cancellationToken);
    }

    /// <summary>Settles the charge and grants its add-on. True when the company is suspended for billing (the caller resumes it after the commit).</summary>
    private bool MarkPaid(IBillingChange change, BillingCharge charge, ChargeSettlement settlement, string? note, Guid? adminId, string? reasonCode, DateTime now)
    {
        if (!charge.MarkPaid(settlement, note, now))
        {
            return false;
        }

        var subscription = change.Subscription;
        if (charge.AddedAiConversations > 0)
        {
            change.Add(AiQuotaGrant.For(charge, _zone.StartOfDayUtc(charge.PeriodStart), _zone.StartOfDayUtc(charge.PeriodEnd)));
        }

        subscription.MarkCurrent();
        subscription.Schedule(now);
        var details = ChargeDetails(charge);
        change.Record(adminId is { } admin
            ? PlatformAuditLog.Record(admin, PlatformAuditLog.Actions.BillingChargePaid, subscription.TenantId, details, reasonCode)
            : PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingChargePaid, subscription.TenantId, ReasonCodes.BillingChargePaid, details));
        return subscription.BillingSuspendedAt is not null;
    }

    private static Dictionary<string, object?> ChargeDetails(BillingCharge charge) => new()
    {
        ["chargeId"] = charge.Id,
        ["periodStart"] = charge.PeriodStart.ToString("yyyy-MM-dd"),
        ["periodEnd"] = charge.PeriodEnd.ToString("yyyy-MM-dd"),
        ["plan"] = charge.PlanCode,
        ["lines"] = charge.Lines,
        ["addOnQuantity"] = charge.AddOnQuantity,
        ["amountInCents"] = charge.AmountInCents,
        ["currency"] = charge.Currency,
        ["status"] = charge.Status.ToString(),
        ["settlement"] = charge.Settlement?.ToString(),
        ["note"] = charge.Note,
    };

    // ------------------------------------------------------------------ the company's lifecycle

    /// <summary>
    /// The dunning step: decides from the state as it is NOW and acts in the same step: the subscription's lock is held from the decision through
    /// the lifecycle call to the record of what it did, so a payment (another instance, an event) cannot land in between: it waits for this
    /// step and then finds a cancelled company (and is flagged for a person). Called at the end of every run and after anything that settles
    /// money. Nothing is suspended or cancelled while an attempt of the overdue charge is pending or of unknown outcome; a company left
    /// suspended with nothing owed (or whose subscription is cancelled) is lifted.
    /// </summary>
    public async Task ApplyDunningAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken: cancellationToken);
        if (change is null || await change.GetTenantFactsAsync(cancellationToken) is not { } tenant || tenant.Status == TenantStatus.Cancelled)
        {
            return;
        }

        var subscription = change.Subscription;
        var open = await change.GetOpenChargeAsync(cancellationToken);
        var pendingOnOpen = open is not null && (await change.GetPendingAttemptsAsync(cancellationToken)).Any(a => a.ChargeId == open.Id);
        var decision = Evaluate(subscription, open, tenant, Today, pendingOnOpen);
        var now = Now;
        switch (decision.Kind)
        {
            case DunningKind.Suspend:
                if (await LifecycleAsync(tenantId, "suspend for billing", l => l.SuspendForBillingAsync(tenantId, ReasonCodes.BillingPastDue, cancellationToken))
                    is not (TenantLifecycleOutcome.Changed or TenantLifecycleOutcome.Unchanged))
                {
                    return; // failed (retried by the next run), or not applicable (the rule does not ask again for this state)
                }

                if (await change.GetOpenChargeAsync(cancellationToken) is null)
                {
                    // Paid during the call (cannot happen while the lock is held; healed all the same).
                    await LifecycleAsync(tenantId, "lift the billing suspension of", l => l.ResumeFromBillingAsync(tenantId, ReasonCodes.BillingPaid, cancellationToken));
                    return;
                }

                subscription.RecordBillingSuspension(now);
                subscription.ClearDunningRestart();
                subscription.Schedule(now);
                break;

            case DunningKind.Resume:
                if (await LifecycleAsync(tenantId, "lift the billing suspension of", l => l.ResumeFromBillingAsync(tenantId, ReasonCodes.BillingPaid, cancellationToken)) is null)
                {
                    return;
                }

                subscription.ClearBillingSuspension();
                subscription.ClearDunningRestart();
                break;

            case DunningKind.Cancel:
                var reasonCode = decision.ReasonCode!;
                if (await LifecycleAsync(tenantId, "cancel", l => l.CancelForNonPaymentAsync(tenantId, reasonCode, cancellationToken)) is null)
                {
                    return;
                }

                var stillOpen = await change.GetOpenChargesAsync(cancellationToken);
                if (reasonCode == ReasonCodes.BillingNonPayment && open is not null && stillOpen.Count == 0)
                {
                    // A payment landed while the company was being cancelled (cannot happen while the lock is held): a person decides.
                    await ReviewItemAsync(change, BillingReviewItem.Flag("cancelled_after_payment", now, tenantId, open.Id), cancellationToken);
                }

                foreach (var unpaid in stillOpen)
                {
                    unpaid.Fail(now);
                    change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingChargeFailed, tenantId, reasonCode, ChargeDetails(unpaid)));
                }

                Cancel(change, null, reasonCode, now);
                await PollOnlyAsync(change, cancellationToken);
                break;

            default:
                return;
        }

        await change.CommitAsync(cancellationToken);
    }

    private async Task<TenantLifecycleOutcome?> LifecycleAsync(Guid tenantId, string what, Func<ITenantBillingLifecycle, Task<TenantLifecycleOutcome>> call)
    {
        try
        {
            if (_scopes is null)
            {
                return await call(_lifecycle);
            }

            // Its own scope, so its own connection and transaction, while this one keeps the subscription locked.
            await using var scope = _scopes.CreateAsyncScope();
            return await call(scope.ServiceProvider.GetRequiredService<ITenantBillingLifecycle>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(LifecycleChangeFailed, "Could not {What} tenant {TenantId} ({Error}); the next run retries.", what, tenantId, ex.GetType().Name);
            return null;
        }
    }

    private static void Cancel(IBillingChange change, Guid? adminId, string reasonCode, DateTime now)
    {
        var subscription = change.Subscription;
        if (!subscription.Cancel(reasonCode, now))
        {
            return;
        }

        var details = new Dictionary<string, object?> { ["subscriptionId"] = subscription.Id, ["periodEnd"] = subscription.CurrentPeriodEnd.ToString("yyyy-MM-dd") };
        change.Record(adminId is { } admin
            ? PlatformAuditLog.Record(admin, PlatformAuditLog.Actions.BillingSubscriptionCancelled, subscription.TenantId, details, reasonCode)
            : PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingSubscriptionCancelled, subscription.TenantId, reasonCode, details));
    }

    // ------------------------------------------------------------------ the owner

    /// <summary>"Pagar ahora": an attempt against the stored method, now. Returns the attempt's status after the gateway answered (Pending for Nequi).</summary>
    public async Task<PaymentAttemptStatus> PayNowAsync(Guid tenantId, Guid chargeId, Guid userId, CancellationToken cancellationToken = default)
    {
        if (!_gateway.IsConfigured)
        {
            throw new BillingException(BillingProblem.GatewayNotConfigured);
        }

        Guid attemptId;
        GatewayChargeRequest request;
        await using (var change = await BeginOrThrowAsync(tenantId, cancellationToken))
        {
            var subscription = change.Subscription;
            await EnsureLiveAsync(change, cancellationToken);
            var charge = await change.GetChargeAsync(chargeId, cancellationToken) ?? throw new BillingException(BillingProblem.ChargeNotFound);
            if (!charge.IsOpen)
            {
                throw new BillingException(BillingProblem.ChargeNotOpen);
            }

            if ((await change.GetAttemptsAsync(charge.Id, cancellationToken)).Any(a => a.IsPending))
            {
                throw new BillingException(BillingProblem.AttemptPending);
            }

            if (subscription.PaymentSourceId is null)
            {
                throw new BillingException(BillingProblem.NoPaymentMethod);
            }

            var attempt = BeginAttempt(change, charge, PaymentAttemptKind.OwnerInitiated, Now, userId);
            subscription.Schedule(Now + _settings.PendingPollInterval);
            await change.CommitAsync(cancellationToken);
            attemptId = attempt.Id;
            request = ChargeRequest(subscription, attempt);
        }

        await ExecuteAttemptAsync(tenantId, attemptId, request, cancellationToken);

        await using var read = await BeginOrThrowAsync(tenantId, cancellationToken);
        return (await read.GetAttemptAsync(attemptId, cancellationToken))?.Status ?? PaymentAttemptStatus.Pending;
    }

    /// <summary>The charge's single-use payment link (PSE and the other manual methods): the one it has while valid, else a new one.</summary>
    public async Task<GatewayPaymentLink> GetPaymentLinkAsync(Guid tenantId, Guid chargeId, CancellationToken cancellationToken = default)
    {
        if (!_gateway.IsConfigured)
        {
            throw new BillingException(BillingProblem.GatewayNotConfigured);
        }

        // Created under the lock on purpose (a rare, owner-driven call): two links for one charge would leave a payable link nobody matches.
        await using var change = await BeginOrThrowAsync(tenantId, cancellationToken);
        EnsureLive(change.Subscription);
        var charge = await change.GetChargeAsync(chargeId, cancellationToken) ?? throw new BillingException(BillingProblem.ChargeNotFound);
        if (!charge.IsOpen)
        {
            throw new BillingException(BillingProblem.ChargeNotOpen);
        }

        var now = Now;
        if (charge.HasUsablePaymentLink(now))
        {
            return new GatewayPaymentLink(charge.PaymentLinkId!, charge.PaymentLinkUrl!);
        }

        var expiresAt = now.AddDays(_settings.PaymentLinkValidDays);
        var link = await _gateway.CreatePaymentLinkAsync(new GatewayPaymentLinkRequest(
            $"Conversia {charge.PeriodStart:yyyy-MM-dd}",
            $"Suscripción Conversia, periodo {charge.PeriodStart:yyyy-MM-dd} a {charge.PeriodEnd:yyyy-MM-dd}",
            charge.AmountInCents, charge.Currency, expiresAt), cancellationToken);
        charge.SetPaymentLink(link.Id, link.Url, expiresAt);
        change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingPaymentLinkCreated, tenantId, ReasonCodes.BillingOwnerRequest, new Dictionary<string, object?>
        {
            ["chargeId"] = charge.Id,
            ["paymentLinkId"] = link.Id,
            ["expiresAt"] = expiresAt,
        }));
        await change.CommitAsync(cancellationToken);
        return link;
    }

    /// <summary>
    /// Replaces the stored payment method by a payment source the gateway creates from the browser's token (with the terms the payer accepted).
    /// The gateway is called before the lock; a charge waiting for a method is then attempted by the next run.
    /// </summary>
    public async Task SetPaymentMethodAsync(Guid tenantId, Guid userId, GatewayPaymentSourceRequest request, string? clientBrand, string? clientLastFour, CancellationToken cancellationToken = default)
    {
        if (!_gateway.IsConfigured)
        {
            throw new BillingException(BillingProblem.GatewayNotConfigured);
        }

        await using (var check = await BeginOrThrowAsync(tenantId, cancellationToken))
        {
            await EnsureLiveAsync(check, cancellationToken);
        }

        var source = await _gateway.CreatePaymentSourceAsync(request, cancellationToken);
        var label = PaymentMethodLabels.For(request.Type, source.Brand ?? clientBrand, source.LastFour ?? clientLastFour);

        await using var change = await BeginOrThrowAsync(tenantId, cancellationToken);
        var subscription = change.Subscription;
        await EnsureLiveAsync(change, cancellationToken);
        var previous = subscription.PaymentMethodLabel;
        var now = Now;
        subscription.SetPaymentMethod(source.Id, request.Type, label, request.CustomerEmail, now);
        subscription.Schedule(now);
        change.Record(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingPaymentMethodChanged, tenantId, ReasonCodes.BillingOwnerRequest, new Dictionary<string, object?>
        {
            ["userId"] = userId,
            ["type"] = request.Type.ToString(),
            ["from"] = previous,
            ["to"] = label,
        }));
        await change.CommitAsync(cancellationToken);
    }

    /// <summary>Cancel at the end of the period, or undo it (the owner or the administrator). Immediate cancellation is the administrator's.</summary>
    public async Task SetCancelAtPeriodEndAsync(Guid tenantId, bool cancel, Guid? adminId, Guid? userId, string? reasonCode, CancellationToken cancellationToken = default)
    {
        await using var change = await BeginOrThrowAsync(tenantId, cancellationToken);
        var subscription = change.Subscription;
        EnsureLive(subscription);
        if (!subscription.SetCancelAtPeriodEnd(cancel))
        {
            return;
        }

        subscription.Schedule(Now);
        var action = cancel ? PlatformAuditLog.Actions.BillingCancellationScheduled : PlatformAuditLog.Actions.BillingCancellationUndone;
        var details = new Dictionary<string, object?> { ["periodEnd"] = subscription.CurrentPeriodEnd.ToString("yyyy-MM-dd"), ["userId"] = userId };
        change.Record(adminId is { } admin
            ? PlatformAuditLog.Record(admin, action, tenantId, details, reasonCode)
            : PlatformAuditLog.RecordSystem(action, tenantId, ReasonCodes.BillingOwnerRequest, details));
        await change.CommitAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ the administrator

    /// <summary>The administrator settles a charge paid outside the gateway (a bank transfer...). Lifts a billing suspension.</summary>
    public async Task MarkPaidManuallyAsync(Guid tenantId, Guid chargeId, Guid adminId, string note, string? reasonCode, CancellationToken cancellationToken = default)
    {
        bool resume;
        await using (var change = await BeginOrThrowAsync(tenantId, cancellationToken))
        {
            var charge = await change.GetChargeAsync(chargeId, cancellationToken) ?? throw new BillingException(BillingProblem.ChargeNotFound);
            if (charge.Status is BillingChargeStatus.Paid or BillingChargeStatus.Void)
            {
                throw new BillingException(BillingProblem.ChargeNotOpen);
            }

            resume = MarkPaid(change, charge, ChargeSettlement.Manual, note, adminId, reasonCode, Now) && await change.GetOpenChargeAsync(cancellationToken) is null;
            await change.CommitAsync(cancellationToken);
        }

        if (resume)
        {
            await ApplyDunningAsync(tenantId, cancellationToken);
        }
    }

    /// <summary>The administrator annuls an unpaid charge: nothing is owed. Lifts a billing suspension when nothing else is open.</summary>
    public async Task VoidChargeAsync(Guid tenantId, Guid chargeId, Guid adminId, string reason, string? reasonCode, CancellationToken cancellationToken = default)
    {
        bool resume;
        await using (var change = await BeginOrThrowAsync(tenantId, cancellationToken))
        {
            var charge = await change.GetChargeAsync(chargeId, cancellationToken) ?? throw new BillingException(BillingProblem.ChargeNotFound);
            if (charge.Status is BillingChargeStatus.Paid or BillingChargeStatus.Void)
            {
                throw new BillingException(BillingProblem.ChargeNotOpen);
            }

            charge.Void(reason, Now);
            change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.BillingChargeVoided, tenantId, ChargeDetails(charge), reasonCode));
            var stillOpen = await change.GetOpenChargeAsync(cancellationToken) is not null;
            if (!stillOpen)
            {
                change.Subscription.MarkCurrent();
            }

            change.Subscription.Schedule(Now);
            resume = !stillOpen && change.Subscription.BillingSuspendedAt is not null;
            await change.CommitAsync(cancellationToken);
        }

        if (resume)
        {
            await ApplyDunningAsync(tenantId, cancellationToken);
        }
    }

    /// <summary>
    /// The administrator ends the subscription now: its unpaid charges are voided and a billing suspension is lifted. The company itself is
    /// left as it is (the administrator changes its status separately).
    /// </summary>
    public async Task CancelNowAsync(Guid tenantId, Guid adminId, string reasonCode, CancellationToken cancellationToken = default)
    {
        bool resume;
        await using (var change = await BeginOrThrowAsync(tenantId, cancellationToken))
        {
            var subscription = change.Subscription;
            EnsureLive(subscription);
            var now = Now;
            foreach (var open in await change.GetOpenChargesAsync(cancellationToken))
            {
                open.Void("Cancelación inmediata de la suscripción", now);
                change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.BillingChargeVoided, tenantId, ChargeDetails(open), reasonCode));
            }

            resume = subscription.BillingSuspendedAt is not null;
            Cancel(change, adminId, reasonCode, now);
            await PollOnlyAsync(change, cancellationToken);
            await change.CommitAsync(cancellationToken);
        }

        if (resume)
        {
            await ApplyDunningAsync(tenantId, cancellationToken);
        }
    }

    /// <summary>
    /// The administrator's verdict on an attempt flagged for review (checked in the gateway's dashboard): <paramref name="failed"/> closes a
    /// pending one as failed (no transaction exists), so the dunning goes on; in every case the review is marked done.
    /// </summary>
    public async Task ReviewAttemptAsync(Guid tenantId, Guid attemptId, Guid adminId, bool failed, string note, CancellationToken cancellationToken = default)
    {
        await using var change = await BeginOrThrowAsync(tenantId, cancellationToken);
        var attempt = await change.GetAttemptAsync(attemptId, cancellationToken) ?? throw new BillingException(BillingProblem.ChargeNotFound);
        var now = Now;
        var charge = await change.GetChargeAsync(attempt.ChargeId, cancellationToken) ?? throw new BillingException(BillingProblem.ChargeNotFound);
        var closed = failed && attempt.Resolve(PaymentAttemptStatus.Error, "verified_no_transaction", now);
        if (closed)
        {
            OnFailure(change, charge, attempt, now);
        }

        attempt.MarkReviewed(now);
        change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.BillingPaymentReviewed, tenantId, new Dictionary<string, object?>
        {
            ["chargeId"] = charge.Id,
            ["attemptId"] = attempt.Id,
            ["closedAsFailed"] = closed,
            ["note"] = Domain.Common.TextRules.Required(note, BillingCharge.NoteMaxLength, BillingCharge.NoteRuleMessage),
        }));
        change.Subscription.Schedule(now);
        await change.CommitAsync(cancellationToken);
    }

    private async Task<IBillingChange> BeginOrThrowAsync(Guid tenantId, CancellationToken cancellationToken)
        => await _unitOfWork.BeginAsync(tenantId, cancellationToken: cancellationToken) ?? throw new BillingException(BillingProblem.SubscriptionNotFound);

    /// <summary>A live subscription of a company that is not cancelled: nothing is charged to a cancelled company, whatever its subscription says.</summary>
    private static async Task EnsureLiveAsync(IBillingChange change, CancellationToken cancellationToken)
    {
        EnsureLive(change.Subscription);
        if (await change.GetTenantFactsAsync(cancellationToken) is not { Status: not TenantStatus.Cancelled })
        {
            throw new BillingException(BillingProblem.SubscriptionCancelled);
        }
    }

    private static void EnsureLive(BillingSubscription subscription)
    {
        if (subscription.IsCancelled)
        {
            throw new BillingException(BillingProblem.SubscriptionCancelled);
        }
    }
}

/// <summary>The masked label of a stored method: "VISA •••• 4242", "Nequi •••• 1111". Nothing else of the card or the number is ever kept.</summary>
public static class PaymentMethodLabels
{
    private static readonly HashSet<string> Brands = new(StringComparer.Ordinal) { "VISA", "MASTERCARD", "AMEX", "DINERS", "DISCOVER" };

    public static string For(PaymentMethodType type, string? brand, string? lastFour)
    {
        var last = lastFour is { Length: >= 4 } digits && digits[^4..].All(char.IsAsciiDigit) ? digits[^4..] : null;
        var masked = last is null ? string.Empty : $" •••• {last}";
        if (type == PaymentMethodType.Nequi)
        {
            return "Nequi" + masked;
        }

        var name = brand?.Trim().ToUpperInvariant() is { } b && Brands.Contains(b) ? b : "Tarjeta";
        return name + masked;
    }
}
