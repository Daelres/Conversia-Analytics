using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

public class BillingPeriodsTests
{
    [Theory]
    [InlineData("2026-11-15", 15, "2026-12-15")]
    [InlineData("2026-01-31", 31, "2026-02-28")]
    [InlineData("2028-01-31", 31, "2028-02-29")]
    [InlineData("2026-02-28", 31, "2026-03-31")] // the anchor comes back once the month has the day
    [InlineData("2026-12-10", 10, "2027-01-10")]
    [InlineData("2026-04-30", 30, "2026-05-30")]
    public void A_period_ends_on_the_anchor_day_of_the_next_month_or_its_last_day(string start, int anchor, string expectedEnd)
        => Assert.Equal(DateOnly.Parse(expectedEnd), BillingPeriods.EndOf(DateOnly.Parse(start), anchor));

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void An_anchor_outside_the_month_is_refused(int anchor)
        => Assert.Throws<DomainException>(() => BillingPeriods.EndOf(new DateOnly(2026, 1, 1), anchor));

    [Fact]
    public void References_are_unique_unguessable_and_recognisable()
    {
        var references = Enumerable.Range(0, 1000).Select(_ => BillingReference.New()).ToList();

        Assert.Equal(1000, references.Distinct().Count());
        Assert.All(references, r => Assert.True(BillingReference.IsValid(r), r));
        Assert.All(references, r => Assert.Equal(BillingReference.Length, r.Length));
        Assert.False(BillingReference.IsValid("cv_" + new string('g', 32)));
        Assert.False(BillingReference.IsValid("xx_" + new string('a', 32)));
        Assert.False(BillingReference.IsValid(null));
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(150)] // not whole pesos
    [InlineData(BillingMoney.MaxPriceInCents + 100)]
    public void A_price_must_be_whole_pesos_within_the_limit(long cents)
        => Assert.Throws<DomainException>(() => BillingMoney.Price(cents));
}

public class BillingSubscriptionTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 15);

    internal static BillingPlan Plan(long pricePerLinePesos = 100_000) => BillingPlan.Create("pro", "Profesional", pricePerLinePesos * 100, 500);

    [Fact]
    public void Without_trial_the_first_period_starts_when_the_job_first_runs()
    {
        var subscription = BillingSubscription.Start(Guid.NewGuid(), Guid.NewGuid(), Today, 0, Now);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(Today, subscription.CurrentPeriodEnd); // "a period starts today"
        Assert.Equal(Now, subscription.NextRunAt);

        subscription.StartPeriod(Today);

        Assert.Equal(Today, subscription.CurrentPeriodStart);
        Assert.Equal(new DateOnly(2026, 12, 15), subscription.CurrentPeriodEnd);
        Assert.Equal(15, subscription.AnchorDay);
    }

    [Fact]
    public void A_trial_is_a_free_first_period_and_the_first_paid_one_follows_it()
    {
        var subscription = BillingSubscription.Start(Guid.NewGuid(), Guid.NewGuid(), Today, 14, Now);

        Assert.Equal(SubscriptionStatus.Trialing, subscription.Status);
        Assert.Equal(new DateOnly(2026, 11, 29), subscription.CurrentPeriodEnd);

        subscription.StartPeriod(subscription.CurrentPeriodEnd);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(new DateOnly(2026, 12, 29), subscription.CurrentPeriodEnd);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(BillingSubscription.MaxTrialDays + 1)]
    public void A_trial_outside_the_limit_is_refused(int days)
        => Assert.Throws<DomainException>(() => BillingSubscription.Start(Guid.NewGuid(), Guid.NewGuid(), Today, days, Now));

    [Fact]
    public void A_period_that_does_not_follow_the_last_one_moves_the_anchor()
    {
        var subscription = BillingSubscription.Start(Guid.NewGuid(), Guid.NewGuid(), Today, 0, Now);
        subscription.StartPeriod(Today);

        subscription.StartPeriod(new DateOnly(2027, 1, 20));

        Assert.Equal(20, subscription.AnchorDay);
        Assert.Equal(new DateOnly(2027, 2, 20), subscription.CurrentPeriodEnd);
        Assert.Throws<DomainException>(() => subscription.StartPeriod(new DateOnly(2027, 2, 1)));
    }

    [Fact]
    public void Billed_lines_are_the_override_or_the_line_quota_and_never_zero()
    {
        var subscription = BillingSubscription.Start(Guid.NewGuid(), Guid.NewGuid(), Today, 0, Now);

        Assert.Equal(4, subscription.BilledLines(4));
        Assert.Equal(1, subscription.BilledLines(0));

        subscription.SetBilledLinesOverride(2);
        Assert.Equal(2, subscription.BilledLines(4));
        Assert.Throws<DomainException>(() => subscription.SetBilledLinesOverride(0));
        Assert.Throws<DomainException>(() => subscription.SetBilledLinesOverride(Tenant.MaxLinesLimit + 1));
    }

    [Fact]
    public void Cancelling_ends_it_once_and_freezes_it()
    {
        var subscription = BillingSubscription.Start(Guid.NewGuid(), Guid.NewGuid(), Today, 0, Now);
        subscription.SetCancelAtPeriodEnd(true);

        Assert.True(subscription.Cancel(ReasonCodes.BillingSubscriptionEnded, Now));
        Assert.False(subscription.Cancel(ReasonCodes.BillingSubscriptionEnded, Now));

        Assert.Equal(SubscriptionStatus.Cancelled, subscription.Status);
        Assert.False(subscription.CancelAtPeriodEnd);
        Assert.Null(subscription.NextRunAt);
        Assert.Throws<DomainException>(() => subscription.ChangePlan(Guid.NewGuid()));
        Assert.Throws<DomainException>(() => subscription.SetCancelAtPeriodEnd(false));
        Assert.Throws<DomainException>(() => subscription.Cancel("Free text!", Now));
        subscription.Schedule(Now);
        Assert.Null(subscription.NextRunAt);
    }

    [Fact]
    public void Past_due_and_current_move_only_between_the_live_statuses()
    {
        var subscription = BillingSubscription.Start(Guid.NewGuid(), Guid.NewGuid(), Today, 0, Now);

        subscription.MarkPastDue();
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
        subscription.MarkCurrent();
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);

        subscription.Cancel(ReasonCodes.BillingNonPayment, Now);
        subscription.MarkPastDue();
        subscription.MarkCurrent();
        Assert.Equal(SubscriptionStatus.Cancelled, subscription.Status);
    }

    [Fact]
    public void An_add_on_needs_a_package_and_a_quantity_within_the_limit()
    {
        var subscription = BillingSubscription.Start(Guid.NewGuid(), Guid.NewGuid(), Today, 0, Now);
        var addOn = Guid.NewGuid();

        subscription.SetAddOn(addOn, 2);
        Assert.Equal((addOn, 2), (subscription.AddOnId!.Value, subscription.AddOnQuantity));

        subscription.SetAddOn(addOn, 0);
        Assert.Null(subscription.AddOnId);

        Assert.Throws<DomainException>(() => subscription.SetAddOn(null, 1));
        Assert.Throws<DomainException>(() => subscription.SetAddOn(addOn, BillingAddOn.MaxQuantity + 1));
    }
}

public class BillingChargeTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 14, 0, 0, DateTimeKind.Utc);

    private static (BillingSubscription Subscription, BillingPlan Plan, BillingAddOn AddOn) Setup()
    {
        var plan = BillingSubscriptionTests.Plan(100_000);
        var subscription = BillingSubscription.Start(Guid.NewGuid(), plan.Id, new DateOnly(2026, 11, 15), 0, Now);
        var addOn = BillingAddOn.Create(plan.Id, "ia-500", "500 conversaciones", 500, 50_000 * 100);
        return (subscription, plan, addOn);
    }

    private static BillingCharge Charge(int lines = 3, int addOns = 2)
    {
        var (subscription, plan, addOn) = Setup();
        return BillingCharge.Create(subscription, plan, lines, addOn, addOns, new DateOnly(2026, 11, 15), new DateOnly(2026, 12, 15), Now);
    }

    [Fact]
    public void The_amount_is_lines_times_price_plus_the_add_ons_and_the_plan_is_copied()
    {
        var charge = Charge(lines: 3, addOns: 2);

        Assert.Equal((3 * 100_000 + 2 * 50_000) * 100L, charge.AmountInCents);
        Assert.Equal("COP", charge.Currency);
        Assert.Equal("pro", charge.PlanCode);
        Assert.Equal(new DateOnly(2026, 11, 15), charge.DueOn);
        Assert.Equal(1000, charge.AddedAiConversations);
        Assert.Equal(BillingChargeStatus.Open, charge.Status);
        Assert.Equal(Now, charge.NextAttemptAt);
    }

    [Fact]
    public void Without_add_on_nothing_extra_is_charged()
    {
        var (subscription, plan, _) = Setup();
        var charge = BillingCharge.Create(subscription, plan, 1, null, 5, new DateOnly(2026, 11, 15), new DateOnly(2026, 12, 15), Now);

        Assert.Equal(100_000 * 100L, charge.AmountInCents);
        Assert.Equal(0, charge.AddedAiConversations);
    }

    [Fact]
    public void Paying_is_idempotent_and_a_manual_payment_needs_a_reason()
    {
        var charge = Charge();

        Assert.Throws<DomainException>(() => charge.MarkPaid(ChargeSettlement.Manual, " ", Now));
        Assert.True(charge.MarkPaid(ChargeSettlement.Manual, "Transferencia bancaria", Now));
        Assert.False(charge.MarkPaid(ChargeSettlement.Gateway, null, Now));

        Assert.Equal(BillingChargeStatus.Paid, charge.Status);
        Assert.Equal(ChargeSettlement.Manual, charge.Settlement);
        Assert.Equal("Transferencia bancaria", charge.Note);
        Assert.Null(charge.NextAttemptAt);
        Assert.Throws<DomainException>(() => charge.Void("x", Now));
    }

    [Fact]
    public void A_voided_charge_cannot_be_paid_and_a_failed_one_still_can()
    {
        var voided = Charge();
        voided.Void("Error de facturación", Now);
        Assert.Throws<DomainException>(() => voided.MarkPaid(ChargeSettlement.Gateway, null, Now));

        var failed = Charge();
        failed.Fail(Now);
        Assert.Equal(BillingChargeStatus.Failed, failed.Status);
        Assert.True(failed.MarkPaid(ChargeSettlement.Manual, "Pagó por transferencia", Now));
    }

    [Fact]
    public void An_automatic_attempt_is_counted_when_it_starts_and_nothing_else_is_scheduled_meanwhile()
    {
        var charge = Charge();

        charge.BeginAutomaticAttempt();

        Assert.Equal(1, charge.AutomaticAttempts);
        Assert.Null(charge.NextAttemptAt);

        charge.ScheduleNextAttempt(Now.AddDays(1));
        Assert.Equal(Now.AddDays(1), charge.NextAttemptAt);

        charge.MarkPaid(ChargeSettlement.Gateway, null, Now);
        charge.ScheduleNextAttempt(Now.AddDays(3));
        Assert.Null(charge.NextAttemptAt);
        Assert.Throws<DomainException>(() => charge.BeginAutomaticAttempt());
    }

    [Fact]
    public void An_attempt_resolves_once_and_keeps_a_short_reason()
    {
        var charge = Charge();
        var attempt = BillingPaymentAttempt.Begin(charge, PaymentAttemptKind.Automatic, PaymentMethodType.Card, Now);

        Assert.True(BillingReference.IsValid(attempt.Reference));
        Assert.Equal(charge.AmountInCents, attempt.AmountInCents);
        Assert.False(attempt.Resolve(PaymentAttemptStatus.Pending, null, Now));
        Assert.True(attempt.Resolve(PaymentAttemptStatus.Declined, new string('x', 500), Now));
        Assert.False(attempt.Resolve(PaymentAttemptStatus.Approved, null, Now));

        Assert.Equal(PaymentAttemptStatus.Declined, attempt.Status);
        Assert.Equal(BillingPaymentAttempt.StatusReasonMaxLength, attempt.StatusReason!.Length);
    }

    [Fact]
    public void A_review_flag_is_raised_once_until_a_person_marks_it_reviewed()
    {
        var attempt = BillingPaymentAttempt.Begin(Charge(), PaymentAttemptKind.Automatic, PaymentMethodType.Card, Now);

        Assert.True(attempt.FlagForReview("duplicate_payment", Now));
        Assert.False(attempt.FlagForReview("approved_after_resolution", Now));
        Assert.Equal("duplicate_payment", attempt.ReviewReason);
        Assert.True(attempt.MarkReviewed(Now));
        Assert.False(attempt.NeedsReview);
        Assert.False(attempt.MarkReviewed(Now));
        Assert.True(attempt.FlagForReview("approved_after_resolution", Now)); // a new problem after the review
    }

    [Fact]
    public void A_restarted_dunning_counts_from_the_later_of_the_due_date_and_the_restart()
    {
        var subscription = BillingSubscription.Start(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 11, 15), 0, Now);
        var due = new DateOnly(2026, 11, 15);

        Assert.Equal(due, subscription.DunningFrom(due));
        subscription.RecordBillingSuspension(Now);
        subscription.RestartDunning(new DateOnly(2026, 11, 25));

        Assert.Null(subscription.BillingSuspendedAt);
        Assert.Equal(new DateOnly(2026, 11, 25), subscription.DunningFrom(due));
        Assert.Equal(new DateOnly(2026, 12, 1), subscription.DunningFrom(new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public void An_attempt_keeps_the_transaction_it_got()
    {
        var attempt = BillingPaymentAttempt.Begin(Charge(), PaymentAttemptKind.OwnerInitiated, PaymentMethodType.Nequi, Now);

        attempt.AttachTransaction("123-abc");
        attempt.AttachTransaction("123-abc");

        Assert.Throws<DomainException>(() => attempt.AttachTransaction("999-xyz"));
    }

    [Fact]
    public void No_attempt_on_a_closed_charge_and_link_payments_come_from_the_gateway()
    {
        var charge = Charge();
        Assert.Throws<DomainException>(() => BillingPaymentAttempt.Begin(charge, PaymentAttemptKind.PaymentLink, null, Now));

        charge.MarkPaid(ChargeSettlement.Gateway, null, Now);
        Assert.Throws<DomainException>(() => BillingPaymentAttempt.Begin(charge, PaymentAttemptKind.Automatic, null, Now));

        var linked = BillingPaymentAttempt.FromPaymentLink(Charge(), "tx-1", null, Now);
        Assert.Equal("tx-1", linked.GatewayTransactionId);
        Assert.Equal("link_tx-1", linked.Reference);
    }

    [Fact]
    public void A_grant_carries_the_paid_add_on_conversations_for_the_period()
    {
        var charge = Charge(addOns: 2);

        var grant = AiQuotaGrant.For(charge, Now, Now.AddDays(30));

        Assert.Equal(1000, grant.Conversations);
        Assert.Throws<DomainException>(() => AiQuotaGrant.For(Charge(addOns: 0), Now, Now.AddDays(30)));
    }

    [Fact]
    public void A_stored_event_is_retried_with_backoff_then_fails()
    {
        var stored = BillingEvent.Receive(new byte[32], "transaction.updated", "tx-1", null, Now);

        stored.BeginAttempt(Now, TimeSpan.FromMinutes(1));
        stored.RegisterFailure("down", Now, maxAttempts: 2, TimeSpan.FromMinutes(1));
        Assert.Equal(BillingEventStatus.Pending, stored.Status);
        stored.BeginAttempt(Now, TimeSpan.FromMinutes(1));
        stored.RegisterFailure("down", Now, maxAttempts: 2, TimeSpan.FromMinutes(1));

        Assert.Equal(BillingEventStatus.Failed, stored.Status);
        Assert.Throws<DomainException>(() => BillingEvent.Receive(new byte[3], "transaction.updated", null, null, Now));
    }

    [Fact]
    public void Catalogue_rules_are_enforced()
    {
        Assert.Throws<DomainException>(() => BillingPlan.Create("Pro Plan", "Pro", 0, 0));
        Assert.Throws<DomainException>(() => BillingPlan.Create("pro", "Pro", 0, -1));
        Assert.Throws<DomainException>(() => BillingAddOn.Create(Guid.NewGuid(), "ia", "IA", 0, 0));
        var plan = BillingPlan.Create(" pro ", "Pro", 0, 0);
        Assert.Equal("pro", plan.Code);
    }
}
