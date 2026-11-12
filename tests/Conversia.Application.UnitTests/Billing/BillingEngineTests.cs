using Conversia.Application.Billing;
using Conversia.Application.Exceptions;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.UnitTests.Billing;

/// <summary>The money state machine of a subscription, driven on fakes and a clock (docs/BILLING.md, "Dunning timeline").</summary>
public class BillingEngineTests
{
    // 2026-11-15 06:00 in Bogota (UTC-5): before the 09:00 attempt hour.
    private static readonly DateTime Start = new(2026, 11, 15, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Due = new(2026, 11, 15);

    [Fact]
    public void The_attempt_hour_is_local()
        => Assert.Equal(new DateTime(2026, 11, 15, 14, 0, 0, DateTimeKind.Utc), new EngineHarness(Start).Engine.AttemptTime(Due));

    [Fact]
    public async Task The_first_run_starts_the_period_with_its_charge_and_waits_for_the_attempt_hour()
    {
        var h = new EngineHarness(Start);
        h.Store.MaxActiveLines = 3;
        h.Subscribe(pricePerLinePesos: 100_000);

        await h.RunAsync();

        var charge = h.OnlyCharge;
        Assert.Equal((Due, new DateOnly(2026, 12, 15)), (charge.PeriodStart, charge.PeriodEnd));
        Assert.Equal(3 * 100_000 * 100L, charge.AmountInCents);
        Assert.Equal(3, charge.Lines);
        Assert.Empty(h.Gateway.Charges);
        Assert.True(h.Subscription.NextRunAt <= h.Engine.AttemptTime(Due));
        Assert.Contains(PlatformAuditLog.Actions.BillingChargeCreated, h.AuditActions);
    }

    [Fact]
    public async Task An_approved_charge_pays_the_period_once()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        await h.RunAsync();
        h.MoveTo(Due, 9);

        await h.RunAsync();
        await h.RunAsync();

        var request = Assert.Single(h.Gateway.Charges);
        var charge = h.OnlyCharge;
        Assert.Equal(charge.AmountInCents, request.AmountInCents);
        Assert.Equal("COP", request.Currency);
        Assert.Equal("src-1", request.PaymentSourceId);
        Assert.Equal("owner@example.test", request.CustomerEmail);
        Assert.True(BillingReference.IsValid(request.Reference));
        Assert.Equal(BillingChargeStatus.Paid, charge.Status);
        Assert.Equal(ChargeSettlement.Gateway, charge.Settlement);
        Assert.Equal(SubscriptionStatus.Active, h.Subscription.Status);
        Assert.Equal(PaymentAttemptStatus.Approved, Assert.Single(h.Store.Attempts).Status);
        Assert.Contains(h.Store.Audit, a => a.Action == PlatformAuditLog.Actions.BillingChargePaid && a.IsSystem && a.ReasonCode == ReasonCodes.BillingChargePaid);
    }

    [Fact]
    public async Task The_next_period_is_charged_when_the_current_one_ends()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.MoveTo(Due, 9);
        await h.RunAsync();

        h.MoveTo(new DateOnly(2026, 12, 15), 9);
        await h.RunAsync();

        Assert.Equal(2, h.Store.Charges.Count);
        Assert.All(h.Store.Charges, c => Assert.Equal(BillingChargeStatus.Paid, c.Status));
        Assert.Equal(new DateOnly(2027, 1, 15), h.Subscription.CurrentPeriodEnd);
    }

    [Fact]
    public async Task Declines_are_retried_on_days_1_3_and_5_then_stop()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextStatus = PaymentAttemptStatus.Declined;
        h.MoveTo(Due, 9);
        await h.RunAsync();

        Assert.Equal(SubscriptionStatus.PastDue, h.Subscription.Status);
        Assert.Equal(h.Engine.AttemptTime(Due.AddDays(1)), h.OnlyCharge.NextAttemptAt);
        Assert.Equal("Fondos insuficientes", h.Store.Attempts[0].StatusReason);

        foreach (var day in new[] { 1, 3, 5 })
        {
            h.MoveTo(Due.AddDays(day), 8);
            await h.RunAsync();
            Assert.Equal(day == 1 ? 1 : day == 3 ? 2 : 3, h.Gateway.Charges.Count); // not before the hour
            h.MoveTo(Due.AddDays(day), 9);
            await h.RunAsync();
        }

        Assert.Equal(4, h.Gateway.Charges.Count);
        Assert.Equal(4, h.OnlyCharge.AutomaticAttempts);
        Assert.Null(h.OnlyCharge.NextAttemptAt);
        Assert.Equal(BillingChargeStatus.Open, h.OnlyCharge.Status);
    }

    [Fact]
    public async Task After_the_grace_period_the_company_is_suspended_for_billing_once_and_a_payment_lifts_it()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextStatus = PaymentAttemptStatus.Declined;
        h.MoveTo(Due, 9);
        await h.RunAsync();

        h.MoveTo(Due.AddDays(6), 23);
        await h.RunAsync();
        Assert.DoesNotContain(h.Lifecycle.Calls, c => c.Call == "suspend");

        h.MoveTo(Due.AddDays(7), 0);
        await h.RunAsync();
        await h.RunAsync();

        Assert.Equal([("suspend", ReasonCodes.BillingPastDue)], h.Lifecycle.Calls);
        Assert.NotNull(h.Subscription.BillingSuspendedAt);
        Assert.Equal(DunningStage.Suspended, DunningPolicy.Stage(h.Subscription.Status, true, true));

        // The owner pays now with a working card.
        h.Gateway.NextStatus = PaymentAttemptStatus.Approved;
        Assert.Equal(PaymentAttemptStatus.Approved, await h.Engine.PayNowAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid()));

        Assert.Equal(("resume", ReasonCodes.BillingPaid), h.Lifecycle.Calls[^1]);
        Assert.Null(h.Subscription.BillingSuspendedAt);
        Assert.Equal(SubscriptionStatus.Active, h.Subscription.Status);
        Assert.Equal(BillingChargeStatus.Paid, h.OnlyCharge.Status);
    }

    [Fact]
    public async Task A_failed_lifecycle_change_is_retried_by_the_next_run()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.Lifecycle.Fail = true;
        h.MoveTo(Due.AddDays(7), 0);
        await h.RunAsync();
        Assert.Null(h.Subscription.BillingSuspendedAt);
        Assert.Contains(h.Logger.Entries, e => e.Event == BillingEngine.LifecycleChangeFailed);

        h.Lifecycle.Fail = false;
        await h.RunAsync();

        Assert.Equal([("suspend", ReasonCodes.BillingPastDue)], h.Lifecycle.Calls);
        Assert.NotNull(h.Subscription.BillingSuspendedAt);
    }

    [Fact]
    public async Task After_the_suspension_window_the_subscription_and_the_company_are_cancelled_for_non_payment()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.MoveTo(Due.AddDays(7), 1);
        await h.RunAsync();

        h.MoveTo(Due.AddDays(36), 23);
        await h.RunAsync();
        Assert.False(h.Subscription.IsCancelled);

        h.MoveTo(Due.AddDays(37), 0);
        await h.RunAsync();

        Assert.Equal(("cancel", ReasonCodes.BillingNonPayment), h.Lifecycle.Calls[^1]);
        Assert.Equal(SubscriptionStatus.Cancelled, h.Subscription.Status);
        Assert.Equal(ReasonCodes.BillingNonPayment, h.Subscription.CancellationReasonCode);
        Assert.Equal(BillingChargeStatus.Failed, h.OnlyCharge.Status);
        Assert.Contains(PlatformAuditLog.Actions.BillingChargeFailed, h.AuditActions);
    }

    [Fact]
    public async Task With_zero_days_the_suspended_company_is_never_cancelled()
    {
        var h = new EngineHarness(Start, new BillingSettings(cancelAfterSuspendedDays: 0));
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.MoveTo(Due.AddDays(7), 1);
        await h.RunAsync();

        h.MoveTo(Due.AddDays(200), 1);
        await h.RunAsync();

        Assert.DoesNotContain(h.Lifecycle.Calls, c => c.Call == "cancel");
        Assert.False(h.Subscription.IsCancelled);
        Assert.Single(h.Store.Charges); // no new period while past due
    }

    [Fact]
    public async Task A_pending_transaction_blocks_any_other_attempt_and_is_resolved_by_polling()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextStatus = PaymentAttemptStatus.Pending; // Nequi waiting for the payer
        h.MoveTo(Due, 9);
        await h.RunAsync();

        h.Advance(TimeSpan.FromSeconds(30));
        await h.RunAsync();
        await Assert.ThrowsAsync<BillingException>(() => h.Engine.PayNowAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid()));
        Assert.Single(h.Gateway.Charges);
        Assert.Equal(0, h.Gateway.Reads); // not polled before the interval

        h.Gateway.Settle("tx-1", PaymentAttemptStatus.Approved);
        h.Advance(h.Settings.PendingPollInterval);
        await h.RunAsync();

        Assert.Single(h.Gateway.Charges);
        Assert.Equal(BillingChargeStatus.Paid, h.OnlyCharge.Status);
    }

    [Fact]
    public async Task The_job_starts_no_automatic_attempt_while_an_owner_payment_is_pending()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        await h.RunAsync();
        h.Gateway.NextStatus = PaymentAttemptStatus.Pending;
        Assert.Equal(PaymentAttemptStatus.Pending, await h.Engine.PayNowAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid()));

        h.MoveTo(Due, 9); // the automatic attempt is due now
        await h.RunAsync();

        Assert.Single(h.Gateway.Charges);
        Assert.Equal(0, h.OnlyCharge.AutomaticAttempts);
    }

    [Fact]
    public async Task An_unknown_outcome_is_looked_up_by_reference_and_never_charged_again_while_it_may_exist()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextOutcome = GatewayChargeOutcome.Unknown;
        h.MoveTo(Due, 9);
        await h.RunAsync();
        var attempt = Assert.Single(h.Store.Attempts);
        Assert.True(attempt.IsPending);

        h.Gateway.NextOutcome = null;
        h.Advance(TimeSpan.FromMinutes(3));
        h.Gateway.ByReference[attempt.Reference] = new GatewayTransaction("tx-late", PaymentAttemptStatus.Approved, null, attempt.AmountInCents, "COP", attempt.Reference, null);
        await h.RunAsync();

        Assert.Single(h.Gateway.Charges);
        Assert.Equal("tx-late", attempt.GatewayTransactionId);
        Assert.Equal(BillingChargeStatus.Paid, h.OnlyCharge.Status);
    }

    [Fact]
    public async Task An_unknown_outcome_stays_pending_blocks_new_attempts_and_goes_to_a_person_after_the_timeout()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextOutcome = GatewayChargeOutcome.Unknown;
        h.MoveTo(Due, 9);
        await h.RunAsync();
        h.Gateway.NextOutcome = null;
        var attempt = h.Store.Attempts[0];

        for (var hour = 0; hour < 30; hour++)
        {
            h.Advance(TimeSpan.FromHours(1));
            await h.RunAsync();
        }

        Assert.True(attempt.IsPending); // nothing found by reference is not proof that nothing was charged
        Assert.Single(h.Gateway.Charges);
        Assert.Equal("unknown_outcome", attempt.ReviewReason);
        Assert.True(attempt.NeedsReview);
        Assert.Single(h.Store.Audit, a => a.Action == PlatformAuditLog.Actions.BillingPaymentNeedsReview);
        Assert.InRange(attempt.Checks, 5, 40); // asked again with backoff, not every run

        // The administrator checked the dashboard: there is no transaction. The dunning goes on.
        await h.Engine.ReviewAttemptAsync(EngineHarness.TenantId, attempt.Id, Guid.NewGuid(), failed: true, "Verificado en el panel de Wompi");

        Assert.Equal(PaymentAttemptStatus.Error, attempt.Status);
        Assert.False(attempt.NeedsReview);
        Assert.NotNull(h.OnlyCharge.NextAttemptAt);
        Assert.Contains(PlatformAuditLog.Actions.BillingPaymentReviewed, h.AuditActions);
    }

    [Fact]
    public async Task An_unknown_outcome_is_looked_up_with_backoff()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextOutcome = GatewayChargeOutcome.Unknown;
        h.MoveTo(Due, 9);
        await h.RunAsync();

        for (var minute = 0; minute < 120; minute += 2)
        {
            h.Advance(TimeSpan.FromMinutes(2));
            await h.RunAsync();
        }

        Assert.InRange(h.Store.Attempts[0].Checks, 3, 7); // 2, 4, 8, 16, 32 minutes apart: not every run
    }

    [Fact]
    public async Task A_payment_approved_in_the_same_run_at_the_end_of_the_grace_period_is_never_followed_by_a_suspension()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextStatus = PaymentAttemptStatus.Pending; // Nequi waiting for the payer
        h.MoveTo(Due, 9);
        await h.RunAsync();

        h.MoveTo(Due.AddDays(7), 0); // the run that would suspend...
        h.Gateway.Settle("tx-1", PaymentAttemptStatus.Approved); // ...polls the payer's approval first
        await h.RunAsync();

        Assert.Equal(BillingChargeStatus.Paid, h.OnlyCharge.Status);
        Assert.DoesNotContain(h.Lifecycle.Calls, c => c.Call == "suspend");
        Assert.Equal(TenantStatus.Active, h.Store.TenantStatus);
        Assert.Null(h.Subscription.BillingSuspendedAt);
    }

    [Fact]
    public async Task A_payment_approved_in_the_same_run_at_the_cancellation_boundary_lifts_the_suspension_instead_of_cancelling()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextStatus = PaymentAttemptStatus.Declined;
        foreach (var day in new[] { 0, 1, 3, 5 })
        {
            h.MoveTo(Due.AddDays(day), 9);
            await h.RunAsync();
        }

        h.MoveTo(Due.AddDays(7), 1);
        await h.RunAsync();
        Assert.Equal(TenantStatus.Suspended, h.Store.TenantStatus);

        h.MoveTo(Due.AddDays(36), 22);
        h.Gateway.NextStatus = PaymentAttemptStatus.Pending; // the owner pays with Nequi the night before the cancellation
        Assert.Equal(PaymentAttemptStatus.Pending, await h.Engine.PayNowAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid()));
        var transactionId = h.Store.Attempts[^1].GatewayTransactionId!;

        h.MoveTo(Due.AddDays(37), 0);
        h.Gateway.Settle(transactionId, PaymentAttemptStatus.Approved);
        await h.RunAsync();

        Assert.DoesNotContain(h.Lifecycle.Calls, c => c.Call == "cancel");
        Assert.Equal(TenantStatus.Active, h.Store.TenantStatus);
        Assert.False(h.Subscription.IsCancelled);
        Assert.Equal(BillingChargeStatus.Paid, h.OnlyCharge.Status);
    }

    [Fact]
    public async Task A_payment_landing_between_the_decision_and_the_suspension_is_healed_at_once()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.MoveTo(Due.AddDays(7), 1);
        var paid = false;
        h.Lifecycle.Before = call =>
        {
            if (call == "suspend" && !paid)
            {
                paid = true;
                h.OnlyCharge.MarkPaid(ChargeSettlement.Manual, "Transferencia", h.Clock.GetUtcNow().UtcDateTime);
            }
        };

        await h.RunAsync();

        Assert.Equal(TenantStatus.Active, h.Store.TenantStatus);
        Assert.Equal(("resume", ReasonCodes.BillingPaid), h.Lifecycle.Calls[^1]);
        Assert.Null(h.Subscription.BillingSuspendedAt);
    }

    [Fact]
    public async Task A_payment_landing_while_the_company_is_being_cancelled_goes_to_a_person()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.MoveTo(Due.AddDays(7), 1);
        await h.RunAsync();
        h.MoveTo(Due.AddDays(37), 1);
        // Impossible in production (the cancellation holds the subscription's lock), so the engine must at least notice it.
        h.Lifecycle.Before = call =>
        {
            if (call == "cancel")
            {
                h.OnlyCharge.MarkPaid(ChargeSettlement.Manual, "Transferencia", h.Clock.GetUtcNow().UtcDateTime);
            }
        };

        await h.RunAsync();

        var item = Assert.Single(h.Store.ReviewItems);
        Assert.Equal("cancelled_after_payment", item.Reason);
        Assert.Equal(h.OnlyCharge.Id, item.ChargeId);
    }

    [Fact]
    public async Task No_suspension_while_an_attempt_of_the_overdue_charge_is_pending_or_unknown()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextOutcome = GatewayChargeOutcome.Unknown;
        h.MoveTo(Due, 9);
        await h.RunAsync();
        h.Gateway.NextOutcome = null;

        h.MoveTo(Due.AddDays(7), 1);
        await h.RunAsync();

        Assert.DoesNotContain(h.Lifecycle.Calls, c => c.Call == "suspend");
        Assert.Equal(TenantStatus.Active, h.Store.TenantStatus);
    }

    [Fact]
    public async Task No_cancellation_while_an_attempt_of_the_overdue_charge_is_pending_or_unknown()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.MoveTo(Due.AddDays(7), 1);
        await h.RunAsync();
        Assert.Equal(TenantStatus.Suspended, h.Store.TenantStatus);
        h.MoveTo(Due.AddDays(20), 10);
        await h.Engine.SetPaymentMethodAsync(EngineHarness.TenantId, Guid.NewGuid(), new GatewayPaymentSourceRequest(PaymentMethodType.Card, "tok", "o@example.test", "a", "b"), "VISA", "4242");
        h.Gateway.NextOutcome = GatewayChargeOutcome.Unknown;
        await h.RunAsync(); // an attempt whose outcome is unknown
        h.Gateway.NextOutcome = null;
        Assert.True(Assert.Single(h.Store.Attempts).IsPending);

        h.MoveTo(Due.AddDays(37), 1);
        await h.RunAsync();

        Assert.DoesNotContain(h.Lifecycle.Calls, c => c.Call == "cancel");
        Assert.False(h.Subscription.IsCancelled);
    }

    [Fact]
    public async Task The_deciding_run_asks_about_pending_attempts_whatever_their_backoff()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextOutcome = GatewayChargeOutcome.Unknown;
        h.MoveTo(Due, 9);
        await h.RunAsync();
        h.Gateway.NextOutcome = null;
        var attempt = h.Store.Attempts[0];
        for (var day = 1; day <= 6; day++)
        {
            h.MoveTo(Due.AddDays(day), 23);
            await h.RunAsync();
        }

        h.Advance(TimeSpan.FromMinutes(59));
        attempt.MarkChecked(h.Clock.GetUtcNow().UtcDateTime); // last asked at 23:59 on day 6: by its backoff, not due again before 00:59

        h.Gateway.ByReference[attempt.Reference] = new GatewayTransaction("tx-late", PaymentAttemptStatus.Approved, null, attempt.AmountInCents, "COP", attempt.Reference, null);
        h.MoveTo(Due.AddDays(7), 0);
        await h.RunAsync();

        Assert.Equal(BillingChargeStatus.Paid, h.OnlyCharge.Status);
        Assert.DoesNotContain(h.Lifecycle.Calls, c => c.Call == "suspend");
    }

    [Fact]
    public async Task An_approval_whose_reference_is_not_ours_becomes_a_durable_review_item()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextOutcome = GatewayChargeOutcome.Unknown;
        h.MoveTo(Due, 9);
        await h.RunAsync();
        var attempt = h.Store.Attempts[0];

        var foreign = new GatewayTransaction("tx-x", PaymentAttemptStatus.Approved, null, attempt.AmountInCents, "COP", "cv_other", null);
        await h.Engine.ApplyAsync(EngineHarness.TenantId, attempt.Id, foreign);
        await h.Engine.ApplyAsync(EngineHarness.TenantId, attempt.Id, foreign); // replayed: one item

        Assert.True(attempt.IsPending);
        var item = Assert.Single(h.Store.ReviewItems);
        Assert.Equal(("reference_mismatch", "tx-x"), (item.Reason, item.TransactionId));
    }

    [Fact]
    public async Task A_company_left_suspended_for_billing_after_its_subscription_was_cancelled_is_lifted()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.Subscription.Cancel(AdminReasonCodes.CustomerRequest, h.Clock.GetUtcNow().UtcDateTime);
        (h.Store.TenantStatus, h.Store.SuspensionCause) = (TenantStatus.Suspended, SuspensionCause.Billing);

        await h.RunAsync();

        Assert.Equal(TenantStatus.Active, h.Store.TenantStatus);
        Assert.Equal(("resume", ReasonCodes.BillingPaid), h.Lifecycle.Calls[^1]);
    }

    [Fact]
    public async Task A_suspension_the_lifecycle_does_not_apply_is_not_recorded()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.MoveTo(Due.AddDays(7), 1);
        // The administrator suspends it by hand between the decision and the call: the billing suspension does not apply.
        h.Lifecycle.Before = call =>
        {
            if (call == "suspend")
            {
                (h.Store.TenantStatus, h.Store.SuspensionCause) = (TenantStatus.Suspended, SuspensionCause.Manual);
            }
        };

        await h.RunAsync();

        Assert.Null(h.Subscription.BillingSuspendedAt);
        Assert.Equal(SuspensionCause.Manual, h.Store.SuspensionCause);
    }

    [Fact]
    public async Task A_company_left_suspended_for_billing_with_nothing_owed_is_lifted_whatever_the_record_says()
    {
        var h = new EngineHarness(Start, new BillingSettings(cancelAfterSuspendedDays: 0));
        h.Subscribe(pricePerLinePesos: 0, withCard: false); // a free period: nothing owed
        (h.Store.TenantStatus, h.Store.SuspensionCause) = (TenantStatus.Suspended, SuspensionCause.Billing);
        Assert.Null(h.Subscription.BillingSuspendedAt);

        await h.RunAsync();

        Assert.Equal(TenantStatus.Active, h.Store.TenantStatus);
        Assert.Equal(("resume", ReasonCodes.BillingPaid), h.Lifecycle.Calls[^1]);
    }

    [Fact]
    public async Task Approved_money_on_an_attempt_already_resolved_goes_to_a_person_once()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextStatus = PaymentAttemptStatus.Declined;
        h.MoveTo(Due, 9);
        await h.RunAsync();
        var approved = h.Gateway.Transactions["tx-1"] with { Status = PaymentAttemptStatus.Approved };

        await h.Engine.ApplyAsync(EngineHarness.TenantId, null, approved);
        await h.Engine.ApplyAsync(EngineHarness.TenantId, null, approved); // replayed

        Assert.Equal("approved_after_resolution", h.Store.Attempts[0].ReviewReason);
        Assert.Single(h.Store.Audit, a => a.Action == PlatformAuditLog.Actions.BillingPaymentNeedsReview);
        Assert.Equal(BillingChargeStatus.Open, h.OnlyCharge.Status);
    }

    [Fact]
    public async Task A_pending_attempt_of_a_charge_settled_by_hand_is_still_polled_and_its_approval_flagged()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextStatus = PaymentAttemptStatus.Pending;
        h.MoveTo(Due, 9);
        await h.RunAsync();
        await h.Engine.MarkPaidManuallyAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid(), "Transferencia", null);

        h.Gateway.Settle("tx-1", PaymentAttemptStatus.Approved);
        h.Advance(h.Settings.PendingPollInterval);
        await h.RunAsync();

        Assert.Equal(PaymentAttemptStatus.Approved, h.Store.Attempts[0].Status);
        Assert.Equal("duplicate_payment", h.Store.Attempts[0].ReviewReason);
    }

    [Fact]
    public async Task A_paused_company_gets_a_billing_hold_that_the_payment_clears()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        h.Store.TenantStatus = TenantStatus.Paused;
        await h.RunAsync();
        h.MoveTo(Due.AddDays(7), 1);

        await h.RunAsync();

        Assert.NotNull(h.Store.BillingHoldSince);
        Assert.Equal(TenantStatus.Paused, h.Store.TenantStatus);
        Assert.NotNull(h.Subscription.BillingSuspendedAt);

        await h.Engine.MarkPaidManuallyAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid(), "Transferencia", null);

        Assert.Null(h.Store.BillingHoldSince);
        Assert.Null(h.Subscription.BillingSuspendedAt);
    }

    [Theory]
    [InlineData(TenantStatus.Suspended)] // by the administrator
    [InlineData(TenantStatus.Pending)]
    public async Task A_company_the_rule_cannot_suspend_is_never_suspended_nor_cancelled_for_billing(TenantStatus status)
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        h.Store.TenantStatus = status;
        h.Store.SuspensionCause = status == TenantStatus.Suspended ? SuspensionCause.Manual : null;
        await h.RunAsync();

        for (var day = 1; day <= 60; day += 3)
        {
            h.MoveTo(Due.AddDays(day), 10);
            await h.RunAsync();
        }

        Assert.Empty(h.Lifecycle.Calls);
        Assert.Null(h.Subscription.BillingSuspendedAt);
        Assert.False(h.Subscription.IsCancelled);
        Assert.True(h.Subscription.NextRunAt <= h.Clock.GetUtcNow().UtcDateTime.AddHours(1)); // looked at again, not forgotten
    }

    [Fact]
    public async Task When_the_administrator_reactivates_a_company_that_still_owes_the_grace_restarts_and_the_cancellation_counts_from_the_new_suspension()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.MoveTo(Due.AddDays(7), 1);
        await h.RunAsync();
        Assert.Equal(TenantStatus.Suspended, h.Store.TenantStatus);

        h.MoveTo(Due.AddDays(10), 10);
        (h.Store.TenantStatus, h.Store.SuspensionCause) = (TenantStatus.Active, null); // the administrator reactivated it
        await h.RunAsync();

        Assert.Equal(TenantStatus.Active, h.Store.TenantStatus);
        Assert.Null(h.Subscription.BillingSuspendedAt);
        Assert.Equal(Due.AddDays(10), h.Subscription.DunningRestartedOn);

        h.MoveTo(Due.AddDays(16), 23);
        await h.RunAsync();
        Assert.Equal(TenantStatus.Active, h.Store.TenantStatus);

        h.MoveTo(Due.AddDays(17), 1);
        await h.RunAsync();
        Assert.Equal(TenantStatus.Suspended, h.Store.TenantStatus);

        h.MoveTo(Due.AddDays(46), 23); // 30 days after the FIRST suspension would have cancelled at Due+37
        await h.RunAsync();
        Assert.False(h.Subscription.IsCancelled);

        h.MoveTo(Due.AddDays(47), 1);
        await h.RunAsync();
        Assert.True(h.Subscription.IsCancelled);
    }

    [Fact]
    public async Task Retries_keep_twelve_hours_apart_when_the_job_was_down()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextStatus = PaymentAttemptStatus.Declined;
        h.MoveTo(Due, 9);
        await h.RunAsync();

        h.MoveTo(Due.AddDays(3), 8); // down since the day-1 retry was due; the day-3 one is an hour away
        await h.RunAsync();

        Assert.Equal(2, h.Gateway.Charges.Count);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime + BillingEngine.MinRetrySpacing, h.OnlyCharge.NextAttemptAt);
    }

    [Theory]
    [InlineData(3, "2026-12-15")] // paid 3 days after the period ended: the calendar continues
    [InlineData(4, "2026-12-19")] // 4 days: the next period starts the day it is paid
    public async Task A_late_payment_restarts_the_calendar_only_beyond_three_days(int daysLate, string expectedStart)
    {
        var h = new EngineHarness(Start, new BillingSettings(cancelAfterSuspendedDays: 0));
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.MoveTo(new DateOnly(2026, 12, 15).AddDays(daysLate), 10);
        await h.Engine.MarkPaidManuallyAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid(), "Efectivo", null);

        await h.RunAsync();

        Assert.Equal(DateOnly.Parse(expectedStart), h.Store.Charges.Single(c => c.IsOpen).PeriodStart);
    }

    [Fact]
    public async Task A_charge_voided_long_after_its_period_restarts_the_calendar_on_that_day()
    {
        var h = new EngineHarness(Start, new BillingSettings(cancelAfterSuspendedDays: 0));
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.MoveTo(new DateOnly(2027, 2, 2), 10);
        await h.Engine.VoidChargeAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid(), "Error de facturación", null);

        await h.RunAsync();

        Assert.Equal(new DateOnly(2027, 2, 2), h.Store.Charges.Single(c => c.IsOpen).PeriodStart);
    }

    [Fact]
    public async Task Nothing_is_charged_nor_stored_for_a_cancelled_company()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        await h.RunAsync();
        h.Store.TenantStatus = TenantStatus.Cancelled;

        var pay = await Assert.ThrowsAsync<BillingException>(() => h.Engine.PayNowAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid()));
        var method = await Assert.ThrowsAsync<BillingException>(() => h.Engine.SetPaymentMethodAsync(
            EngineHarness.TenantId, Guid.NewGuid(), new GatewayPaymentSourceRequest(PaymentMethodType.Card, "tok", "o@example.test", "a", "b"), null, null));

        Assert.Equal(BillingProblem.SubscriptionCancelled, pay.Problem);
        Assert.Equal(BillingProblem.SubscriptionCancelled, method.Problem);
        Assert.Empty(h.Gateway.Charges);
        Assert.Empty(h.Gateway.Sources);
    }

    [Fact]
    public async Task A_refusal_without_transaction_counts_as_a_failed_attempt()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextOutcome = GatewayChargeOutcome.Refused;
        h.MoveTo(Due, 9);
        await h.RunAsync();

        Assert.Equal(PaymentAttemptStatus.Error, h.Store.Attempts[0].Status);
        Assert.Equal(h.Engine.AttemptTime(Due.AddDays(1)), h.OnlyCharge.NextAttemptAt);
    }

    [Fact]
    public async Task An_approval_for_another_amount_pays_nothing_and_goes_to_a_person()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.ReportedAmount = 100;
        h.MoveTo(Due, 9);
        await h.RunAsync();

        Assert.Equal(BillingChargeStatus.Open, h.OnlyCharge.Status);
        Assert.Equal("amount_mismatch", h.Store.Attempts[0].StatusReason);
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Error && e.Event == BillingEngine.PaymentNeedsReview);
        Assert.Contains(PlatformAuditLog.Actions.BillingPaymentNeedsReview, h.AuditActions);
    }

    [Fact]
    public async Task A_second_approval_of_a_paid_charge_is_flagged_and_grants_nothing_twice()
    {
        var addOnPlan = Guid.NewGuid();
        var h = new EngineHarness(Start);
        var plan = h.Subscribe();
        var addOn = BillingAddOn.Create(plan.Id, "ia-500", "500 conversaciones", 500, 40_000 * 100);
        h.Store.AddOns.Add(addOn);
        h.Subscription.SetAddOn(addOn.Id, 2);
        h.Gateway.NextStatus = PaymentAttemptStatus.Pending;
        h.MoveTo(Due, 9);
        await h.RunAsync();

        // Paid by bank transfer meanwhile, then the pending card payment is approved too.
        await h.Engine.MarkPaidManuallyAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid(), "Transferencia", null);
        await h.Engine.ApplyAsync(EngineHarness.TenantId, null, h.Gateway.Transactions["tx-1"] with { Status = PaymentAttemptStatus.Approved });

        var grant = Assert.Single(h.Store.Grants);
        Assert.Equal(1000, grant.Conversations);
        Assert.Equal(new DateTime(2026, 11, 15, 5, 0, 0, DateTimeKind.Utc), grant.ValidFrom);
        Assert.Equal(new DateTime(2026, 12, 15, 5, 0, 0, DateTimeKind.Utc), grant.ValidUntil);
        Assert.Contains(h.Store.Audit, a => a.Action == PlatformAuditLog.Actions.BillingPaymentNeedsReview && a.Details.Contains("duplicate_payment"));
        Assert.Equal((2 * 40_000 + 100_000 * 2) * 100L, h.OnlyCharge.AmountInCents);
        _ = addOnPlan;
    }

    [Fact]
    public async Task A_replayed_answer_changes_nothing()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.MoveTo(Due, 9);
        await h.RunAsync();
        var audits = h.Store.Audit.Count;

        await h.Engine.ApplyAsync(EngineHarness.TenantId, null, h.Gateway.Transactions["tx-1"]);
        await h.Engine.ApplyAsync(EngineHarness.TenantId, null, h.Gateway.Transactions["tx-1"] with { Status = PaymentAttemptStatus.Declined });

        Assert.Equal(audits, h.Store.Audit.Count);
        Assert.Equal(BillingChargeStatus.Paid, h.OnlyCharge.Status);
        Assert.Equal(PaymentAttemptStatus.Approved, h.Store.Attempts[0].Status);
    }

    [Fact]
    public async Task A_transaction_with_another_reference_is_never_applied_to_an_attempt()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextOutcome = GatewayChargeOutcome.Unknown;
        h.MoveTo(Due, 9);
        await h.RunAsync();
        var attempt = h.Store.Attempts[0];

        var foreign = new GatewayTransaction("tx-x", PaymentAttemptStatus.Approved, null, attempt.AmountInCents, "COP", "cv_other", null);
        await h.Engine.ApplyAsync(EngineHarness.TenantId, attempt.Id, foreign);
        await h.Engine.ApplyAsync(EngineHarness.TenantId, attempt.Id, foreign); // replayed: one item

        Assert.True(attempt.IsPending);
        Assert.Equal(BillingChargeStatus.Open, h.OnlyCharge.Status);
    }

    [Fact]
    public async Task A_free_plan_is_settled_without_the_gateway()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(pricePerLinePesos: 0, withCard: false);

        await h.RunAsync();

        Assert.Equal(ChargeSettlement.Free, h.OnlyCharge.Settlement);
        Assert.Empty(h.Gateway.Charges);
    }

    [Fact]
    public async Task A_trial_is_not_charged_until_it_ends()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(trialDays: 14);

        await h.RunAsync();
        Assert.Empty(h.Store.Charges);
        Assert.Equal(SubscriptionStatus.Trialing, h.Subscription.Status);

        h.MoveTo(Due.AddDays(14), 9);
        await h.RunAsync();

        Assert.Equal(Due.AddDays(14), h.OnlyCharge.PeriodStart);
        Assert.Equal(BillingChargeStatus.Paid, h.OnlyCharge.Status);
        Assert.Equal(SubscriptionStatus.Active, h.Subscription.Status);
    }

    [Fact]
    public async Task Cancelling_at_period_end_ends_the_company_when_the_period_ends()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.MoveTo(Due, 9);
        await h.RunAsync();
        await h.Engine.SetCancelAtPeriodEndAsync(EngineHarness.TenantId, true, null, Guid.NewGuid(), null);

        h.MoveTo(new DateOnly(2026, 12, 14), 23);
        await h.RunAsync();
        Assert.False(h.Subscription.IsCancelled);

        h.MoveTo(new DateOnly(2026, 12, 15), 0);
        await h.RunAsync();

        Assert.Equal(("cancel", ReasonCodes.BillingSubscriptionEnded), h.Lifecycle.Calls[^1]);
        Assert.True(h.Subscription.IsCancelled);
        Assert.Single(h.Store.Charges); // the period that did not start is not charged
    }

    [Fact]
    public async Task Undoing_the_cancellation_keeps_it_running()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        await h.Engine.SetCancelAtPeriodEndAsync(EngineHarness.TenantId, true, null, Guid.NewGuid(), null);
        await h.Engine.SetCancelAtPeriodEndAsync(EngineHarness.TenantId, false, null, Guid.NewGuid(), null);

        await h.RunAsync();

        Assert.False(h.Subscription.IsCancelled);
        Assert.Single(h.Store.Charges);
        Assert.Contains(PlatformAuditLog.Actions.BillingCancellationUndone, h.AuditActions);
    }

    [Fact]
    public async Task A_period_paid_long_after_it_ended_restarts_the_calendar_on_the_next_run()
    {
        var h = new EngineHarness(Start, new BillingSettings(cancelAfterSuspendedDays: 0));
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.MoveTo(new DateOnly(2027, 1, 20), 10);
        await h.Engine.MarkPaidManuallyAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid(), "Pago en efectivo", null);

        await h.RunAsync();

        var next = h.Store.Charges.Single(c => c.IsOpen);
        Assert.Equal(new DateOnly(2027, 1, 20), next.PeriodStart);
        Assert.Equal(new DateOnly(2027, 2, 20), next.PeriodEnd);
    }

    [Fact]
    public async Task A_company_cancelled_by_the_administrator_is_no_longer_charged()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.Store.TenantStatus = TenantStatus.Cancelled;

        await h.RunAsync();

        Assert.True(h.Subscription.IsCancelled);
        Assert.Equal(BillingChargeStatus.Void, h.OnlyCharge.Status);
        Assert.Empty(h.Lifecycle.Calls);
        h.MoveTo(new DateOnly(2027, 3, 1), 10);
        await h.RunAsync();
        Assert.Single(h.Store.Charges);
    }

    [Fact]
    public async Task A_company_cancelled_by_the_administrator_is_never_charged_again_and_its_open_charge_is_voided()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        await h.RunAsync(); // 06:00: the charge exists, its attempt is due at 09:00
        h.Store.TenantStatus = TenantStatus.Cancelled; // the administrator cancelled the company (only the company row)

        h.MoveTo(Due, 9);
        await h.RunAsync();
        h.MoveTo(new DateOnly(2027, 1, 20), 9);
        await h.RunAsync();

        Assert.True(h.Subscription.IsCancelled);
        Assert.Equal(BillingChargeStatus.Void, h.OnlyCharge.Status);
        Assert.Empty(h.Gateway.Charges);
        Assert.Empty(h.Store.Attempts);
    }

    [Fact]
    public async Task A_pending_payment_of_a_cancelled_company_is_still_polled_and_its_approval_flagged()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Gateway.NextStatus = PaymentAttemptStatus.Pending;
        h.MoveTo(Due, 9);
        await h.RunAsync();
        h.Store.TenantStatus = TenantStatus.Cancelled;
        h.Advance(TimeSpan.FromMinutes(1));
        await h.RunAsync();
        Assert.True(h.Subscription.IsCancelled);
        Assert.NotNull(h.Subscription.NextRunAt); // the job comes back while an attempt is pending

        h.Gateway.Settle("tx-1", PaymentAttemptStatus.Approved);
        h.Advance(h.Settings.PendingPollInterval);
        await h.RunAsync();

        var attempt = Assert.Single(h.Store.Attempts);
        Assert.Equal(PaymentAttemptStatus.Approved, attempt.Status);
        Assert.Equal("paid_after_void", attempt.ReviewReason);
        Assert.Single(h.Gateway.Charges);
    }

    [Fact]
    public async Task Another_instance_holding_the_subscription_is_left_alone()
    {
        var h = new EngineHarness(Start);
        h.Subscribe();
        h.Store.HeldByAnotherInstance = true;

        await h.RunAsync();

        Assert.Empty(h.Store.Charges);
        Assert.Equal(0, h.Store.Commits);
    }

    [Fact]
    public async Task Without_gateway_keys_charges_are_created_but_never_attempted()
    {
        var h = new EngineHarness(Start);
        h.Gateway.IsConfigured = false;
        h.Subscribe();
        h.MoveTo(Due, 10);

        await h.RunAsync();

        Assert.Single(h.Store.Charges);
        Assert.Empty(h.Gateway.Charges);
        await Assert.ThrowsAsync<BillingException>(() => h.Engine.PayNowAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task A_payment_link_is_created_once_and_its_payment_settles_the_charge()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();
        var charge = h.OnlyCharge;

        var first = await h.Engine.GetPaymentLinkAsync(EngineHarness.TenantId, charge.Id);
        var second = await h.Engine.GetPaymentLinkAsync(EngineHarness.TenantId, charge.Id);

        Assert.Equal(first, second);
        var link = Assert.Single(h.Gateway.Links);
        Assert.Equal(charge.AmountInCents, link.AmountInCents);

        await h.Engine.ApplyAsync(EngineHarness.TenantId, null, new GatewayTransaction("tx-pse", PaymentAttemptStatus.Approved, null, charge.AmountInCents, "COP", "wompi-ref", first.Id), paymentLinkChargeId: charge.Id);

        Assert.Equal(BillingChargeStatus.Paid, charge.Status);
        var attempt = Assert.Single(h.Store.Attempts);
        Assert.Equal(PaymentAttemptKind.PaymentLink, attempt.Kind);
    }

    [Fact]
    public async Task A_link_payment_is_applied_only_when_the_re_read_transaction_names_this_charges_link()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();
        var charge = h.OnlyCharge;
        await h.Engine.GetPaymentLinkAsync(EngineHarness.TenantId, charge.Id);

        await h.Engine.ApplyAsync(EngineHarness.TenantId, null, new GatewayTransaction("tx-other", PaymentAttemptStatus.Approved, null, charge.AmountInCents, "COP", "ref", "another-link"), paymentLinkChargeId: charge.Id);
        await h.Engine.ApplyAsync(EngineHarness.TenantId, null, new GatewayTransaction("tx-none", PaymentAttemptStatus.Approved, null, charge.AmountInCents, "COP", "ref", null), paymentLinkChargeId: charge.Id);

        Assert.Empty(h.Store.Attempts);
        Assert.Equal(BillingChargeStatus.Open, charge.Status);
    }

    [Fact]
    public async Task A_new_payment_method_is_stored_masked_and_a_waiting_charge_is_attempted()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        h.MoveTo(Due, 10);
        await h.RunAsync();
        Assert.Empty(h.Gateway.Charges);

        await h.Engine.SetPaymentMethodAsync(
            EngineHarness.TenantId, Guid.NewGuid(), new GatewayPaymentSourceRequest(PaymentMethodType.Card, "tok_test_1", "owner@example.test", "acc", "pda"), "visa", "4242");
        await h.RunAsync();

        Assert.Equal("VISA •••• 4242", h.Subscription.PaymentMethodLabel);
        Assert.Equal("src-1", h.Subscription.PaymentSourceId);
        Assert.Single(h.Gateway.Charges);
        Assert.Equal(BillingChargeStatus.Paid, h.OnlyCharge.Status);
    }

    [Fact]
    public async Task Voiding_the_only_open_charge_lifts_a_billing_suspension()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();
        h.MoveTo(Due.AddDays(7), 1);
        await h.RunAsync();

        await h.Engine.VoidChargeAsync(EngineHarness.TenantId, h.OnlyCharge.Id, Guid.NewGuid(), "Error de facturación", null);

        Assert.Equal(BillingChargeStatus.Void, h.OnlyCharge.Status);
        Assert.Equal(SubscriptionStatus.Active, h.Subscription.Status);
        Assert.Equal(("resume", ReasonCodes.BillingPaid), h.Lifecycle.Calls[^1]);
        Assert.Contains(h.Store.Audit, a => a.Action == PlatformAuditLog.Actions.BillingChargeVoided && !a.IsSystem);
    }

    [Fact]
    public async Task Cancelling_now_voids_what_is_owed_and_leaves_the_company_status_to_the_administrator()
    {
        var h = new EngineHarness(Start);
        h.Subscribe(withCard: false);
        await h.RunAsync();

        await h.Engine.CancelNowAsync(EngineHarness.TenantId, Guid.NewGuid(), AdminReasonCodes.CustomerRequest);

        Assert.True(h.Subscription.IsCancelled);
        Assert.Equal(BillingChargeStatus.Void, h.OnlyCharge.Status);
        Assert.DoesNotContain(h.Lifecycle.Calls, c => c.Call == "cancel");
        await h.RunAsync();
        Assert.Single(h.Store.Charges);
    }

    [Theory]
    [InlineData(PaymentMethodType.Card, "visa", "4242", "VISA •••• 4242")]
    [InlineData(PaymentMethodType.Card, "weird", "12", "Tarjeta")]
    [InlineData(PaymentMethodType.Nequi, null, "3991111111", "Nequi •••• 1111")]
    public void Labels_keep_only_the_brand_and_the_last_four(PaymentMethodType type, string? brand, string? last, string expected)
        => Assert.Equal(expected, PaymentMethodLabels.For(type, brand, last));
}
