using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>The full lifecycle of a company (docs/PLATFORM_ADMIN.md, "Lifecycle of a company"): pause, billing suspension, cancellation and deletion.</summary>
public class TenantLifecycleTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Admin = Guid.NewGuid();

    private static Tenant ActiveTenant()
    {
        var tenant = Tenant.Create("Clínica Demo");
        tenant.ChangeStatus(TenantStatus.Active, Admin, null, Now.AddDays(-30));
        return tenant;
    }

    private static Tenant InStatus(TenantStatus status)
    {
        var tenant = ActiveTenant();
        switch (status)
        {
            case TenantStatus.Pending:
                return Tenant.Create("Clínica Pendiente");
            case TenantStatus.Active:
                return tenant;
            case TenantStatus.Cancelled:
                tenant.Cancel(Admin, null, null, Now.AddDays(-1), 30);
                return tenant;
            default:
                tenant.ChangeStatus(status, Admin, null, Now.AddDays(-1));
                return tenant;
        }
    }

    [Theory]
    [InlineData(TenantStatus.Pending, TenantStatus.Paused, false)]
    [InlineData(TenantStatus.Pending, TenantStatus.Cancelled, true)]
    [InlineData(TenantStatus.Active, TenantStatus.Paused, true)]
    [InlineData(TenantStatus.Active, TenantStatus.Cancelled, true)]
    [InlineData(TenantStatus.Paused, TenantStatus.Active, true)]
    [InlineData(TenantStatus.Paused, TenantStatus.Suspended, true)]
    [InlineData(TenantStatus.Paused, TenantStatus.Cancelled, true)]
    [InlineData(TenantStatus.Paused, TenantStatus.Paused, false)]
    [InlineData(TenantStatus.Paused, TenantStatus.Pending, false)]
    [InlineData(TenantStatus.Suspended, TenantStatus.Paused, false)]
    [InlineData(TenantStatus.Suspended, TenantStatus.Cancelled, true)]
    [InlineData(TenantStatus.Cancelled, TenantStatus.Active, true)]
    [InlineData(TenantStatus.Cancelled, TenantStatus.Paused, false)]
    [InlineData(TenantStatus.Cancelled, TenantStatus.Suspended, false)]
    [InlineData(TenantStatus.Cancelled, TenantStatus.Cancelled, false)]
    [InlineData(TenantStatus.Cancelled, TenantStatus.Pending, false)]
    public void TheNewTransitions_AreExactlyTheDocumentedOnes(TenantStatus from, TenantStatus to, bool allowed)
        => Assert.Equal(allowed, Tenant.CanChangeStatus(from, to));

    [Fact]
    public void AnAdministratorSuspension_IsManual_AndReactivationClearsTheCause()
    {
        var tenant = ActiveTenant();

        tenant.ChangeStatus(TenantStatus.Suspended, Admin, "Revisión", Now);
        Assert.Equal(SuspensionCause.Manual, tenant.SuspensionCause);

        tenant.ChangeStatus(TenantStatus.Active, Admin, null, Now.AddHours(1));
        Assert.Null(tenant.SuspensionCause);
        Assert.Equal(TenantStatus.Active, tenant.Status);
    }

    [Fact]
    public void ChangeStatus_CannotCancel_BecauseCancellingNeedsTheRetentionWindow()
    {
        var tenant = ActiveTenant();

        Assert.Throws<DomainException>(() => tenant.ChangeStatus(TenantStatus.Cancelled, Admin, null, Now));
        Assert.Equal(TenantStatus.Active, tenant.Status);
    }

    [Fact]
    public void Pause_KeepsIngesting_ButStopsTheAnalysisAndTheUsers()
    {
        var tenant = ActiveTenant();

        tenant.ChangeStatus(TenantStatus.Paused, Admin, "Vacaciones colectivas", Now);

        Assert.Equal(TenantStatus.Paused, tenant.Status);
        Assert.True(tenant.AcceptsWebhooks);
        Assert.False(tenant.IsOperational);
        Assert.Null(tenant.SuspensionCause);
    }

    [Theory]
    [InlineData(TenantStatus.Pending, false)]
    [InlineData(TenantStatus.Active, true)]
    [InlineData(TenantStatus.Paused, true)]
    [InlineData(TenantStatus.Suspended, false)]
    [InlineData(TenantStatus.Cancelled, false)]
    public void WhoAcceptsWebhooks_FollowsTheStatus(TenantStatus status, bool accepts)
        => Assert.Equal(accepts, InStatus(status).AcceptsWebhooks);

    [Fact]
    public void Cancel_SchedulesTheDeletion_AfterTheRetentionWindow()
    {
        var tenant = ActiveTenant();

        tenant.Cancel(Admin, "Fin del contrato", null, Now, 30);

        Assert.Equal(TenantStatus.Cancelled, tenant.Status);
        Assert.Equal(Now, tenant.CancelledAt);
        Assert.Equal(Now.AddDays(30), tenant.DataDeletionDueAt);
        Assert.Null(tenant.DataDeletionStartedAt);
        Assert.Equal(Admin, tenant.StatusChangedBy);
        Assert.Equal("Fin del contrato", tenant.StatusReason);
        Assert.False(tenant.AcceptsWebhooks);
        Assert.False(tenant.IsDataDeletionDue(Now.AddDays(30).AddSeconds(-1)));
        Assert.True(tenant.IsDataDeletionDue(Now.AddDays(30)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(Tenant.MaxDeletionRetentionDays + 1)]
    public void Cancel_RefusesARetentionOutsideItsRange(int days)
    {
        var tenant = ActiveTenant();

        Assert.Throws<DomainException>(() => tenant.Cancel(Admin, null, null, Now, days));
        Assert.Equal(TenantStatus.Active, tenant.Status);
    }

    [Fact]
    public void Cancel_ByTheSystem_RecordsNoAdministrator_AndItsReasonCode()
    {
        var tenant = ActiveTenant();

        tenant.Cancel(null, null, ReasonCodes.BillingNonPayment, Now, 30);

        Assert.Null(tenant.StatusChangedBy);
        Assert.Equal(ReasonCodes.BillingNonPayment, tenant.StatusReasonCode);
        Assert.Null(tenant.StatusReason);
    }

    [Fact]
    public void Cancel_FromCancelled_IsNotATransition()
    {
        var tenant = InStatus(TenantStatus.Cancelled);

        Assert.Throws<DomainException>(() => tenant.Cancel(Admin, null, null, Now, 30));
    }

    [Fact]
    public void ReactivatingACancelledCompany_BeforeTheDeletion_ClearsTheSchedule()
    {
        var tenant = InStatus(TenantStatus.Cancelled);

        tenant.ChangeStatus(TenantStatus.Active, Admin, "Renovó", Now);

        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Null(tenant.CancelledAt);
        Assert.Null(tenant.DataDeletionDueAt);
        Assert.False(tenant.IsDataDeletionDue(Now.AddYears(1)));
    }

    [Fact]
    public void OnceTheDeletionStarted_TheCompanyCanNoLongerBeReactivated()
    {
        var tenant = InStatus(TenantStatus.Cancelled);

        Assert.True(tenant.BeginDataDeletion(Now));
        Assert.False(tenant.BeginDataDeletion(Now.AddMinutes(1))); // idempotent: the first start stands
        Assert.Equal(Now, tenant.DataDeletionStartedAt);

        Assert.False(Tenant.CanReactivate(tenant));
        Assert.Throws<DomainException>(() => tenant.ChangeStatus(TenantStatus.Active, Admin, null, Now));
        Assert.Equal(TenantStatus.Cancelled, tenant.Status);
        Assert.True(tenant.IsDataDeletionDue(Now.AddDays(-100))); // started: due whatever the date
    }

    [Theory]
    [InlineData(TenantStatus.Pending)]
    [InlineData(TenantStatus.Active)]
    [InlineData(TenantStatus.Paused)]
    [InlineData(TenantStatus.Suspended)]
    public void OnlyACancelledCompanyCanBeDeleted(TenantStatus status)
    {
        var tenant = InStatus(status);

        Assert.Throws<DomainException>(() => tenant.BeginDataDeletion(Now));
        Assert.Null(tenant.DataDeletionStartedAt);
    }

    [Fact]
    public void SuspendForBilling_FromActive_KeepsIngesting_AndIsIdempotent()
    {
        var tenant = ActiveTenant();

        Assert.Equal(TenantLifecycleOutcome.Changed, tenant.SuspendForBilling(ReasonCodes.BillingPastDue, Now));

        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        Assert.Equal(SuspensionCause.Billing, tenant.SuspensionCause);
        Assert.Null(tenant.StatusChangedBy);
        Assert.Equal(ReasonCodes.BillingPastDue, tenant.StatusReasonCode);
        Assert.True(tenant.AcceptsWebhooks);
        Assert.False(tenant.IsOperational);

        Assert.Equal(TenantLifecycleOutcome.Unchanged, tenant.SuspendForBilling(ReasonCodes.BillingPastDue, Now.AddHours(1)));
        Assert.Equal(Now, tenant.StatusChangedAt);
    }

    [Theory]
    [InlineData(TenantStatus.Pending)]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Cancelled)]
    public void SuspendForBilling_LeavesEveryOtherStatusAlone(TenantStatus status)
    {
        var tenant = InStatus(status);
        var before = tenant.StatusChangedAt;

        Assert.Equal(TenantLifecycleOutcome.NotApplicable, tenant.SuspendForBilling(ReasonCodes.BillingPastDue, Now));

        Assert.Equal(status, tenant.Status);
        Assert.Equal(before, tenant.StatusChangedAt);
    }

    [Fact]
    public void ResumeFromBilling_LiftsOnlyABillingSuspension()
    {
        var billing = ActiveTenant();
        billing.SuspendForBilling(ReasonCodes.BillingPastDue, Now);
        var manual = InStatus(TenantStatus.Suspended);

        Assert.Equal(TenantLifecycleOutcome.Changed, billing.ResumeFromBilling(ReasonCodes.BillingPaid, Now.AddDays(1)));
        Assert.Equal(TenantStatus.Active, billing.Status);
        Assert.Null(billing.SuspensionCause);
        Assert.Equal(ReasonCodes.BillingPaid, billing.StatusReasonCode);

        Assert.Equal(TenantLifecycleOutcome.Unchanged, billing.ResumeFromBilling(ReasonCodes.BillingPaid, Now.AddDays(2)));

        // An administrator's suspension is the administrator's to lift: a payment never does it.
        Assert.Equal(TenantLifecycleOutcome.NotApplicable, manual.ResumeFromBilling(ReasonCodes.BillingPaid, Now));
        Assert.Equal(TenantStatus.Suspended, manual.Status);
        Assert.Equal(SuspensionCause.Manual, manual.SuspensionCause);
    }

    [Theory]
    [InlineData(TenantStatus.Pending, TenantLifecycleOutcome.NotApplicable)]
    [InlineData(TenantStatus.Active, TenantLifecycleOutcome.Changed)]
    [InlineData(TenantStatus.Paused, TenantLifecycleOutcome.Changed)]
    [InlineData(TenantStatus.Suspended, TenantLifecycleOutcome.Changed)]
    [InlineData(TenantStatus.Cancelled, TenantLifecycleOutcome.Unchanged)]
    public void CancelForNonPayment_FollowsTheTable(TenantStatus status, TenantLifecycleOutcome expected)
    {
        var tenant = InStatus(status);

        Assert.Equal(expected, tenant.CancelForNonPayment(ReasonCodes.BillingNonPayment, Now, 30));

        Assert.Equal(expected == TenantLifecycleOutcome.NotApplicable ? status : TenantStatus.Cancelled, tenant.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Billing.Past_Due")]
    [InlineData("billing past due")]
    [InlineData(".billing")]
    [InlineData("billing.")]
    public void AReasonCode_IsAStableLowercaseCode_NotFreeText(string code)
        => Assert.Throws<DomainException>(() => ActiveTenant().SuspendForBilling(code, Now));

    [Fact]
    public void AReasonCode_IsAtMost64Characters()
    {
        Assert.True(ReasonCodes.IsValid(new string('a', ReasonCodes.MaxLength)));
        Assert.False(ReasonCodes.IsValid(new string('a', ReasonCodes.MaxLength + 1)));
        Assert.True(ReasonCodes.IsValid("billing.past_due"));
        Assert.True(ReasonCodes.IsValid("deletion.completed"));
    }

    // ---- The full grid: six starting points (Suspended split by cause) by the five statuses -----------------

    public enum From
    {
        Pending,
        Active,
        Paused,
        SuspendedManual,
        SuspendedBilling,
        Cancelled,
    }

    private static Tenant Start(From from)
    {
        switch (from)
        {
            case From.Pending:
                return Tenant.Create("Clínica Pendiente");
            case From.SuspendedBilling:
                var billing = ActiveTenant();
                billing.SuspendForBilling(ReasonCodes.BillingPastDue, Now.AddDays(-1));
                return billing;
            case From.SuspendedManual:
                return InStatus(TenantStatus.Suspended);
            default:
                return InStatus(Enum.Parse<TenantStatus>(from.ToString()));
        }
    }

    // The documented table (docs/PLATFORM_ADMIN.md, "Transitions"), every cell. Cancelling goes through Cancel; the rest through ChangeStatus.
    public static TheoryData<From, TenantStatus, bool> Grid()
    {
        var allowed = new HashSet<(From, TenantStatus)>
        {
            (From.Pending, TenantStatus.Active), (From.Pending, TenantStatus.Suspended), (From.Pending, TenantStatus.Cancelled),
            (From.Active, TenantStatus.Paused), (From.Active, TenantStatus.Suspended), (From.Active, TenantStatus.Cancelled),
            (From.Paused, TenantStatus.Active), (From.Paused, TenantStatus.Suspended), (From.Paused, TenantStatus.Cancelled),
            (From.SuspendedManual, TenantStatus.Active), (From.SuspendedManual, TenantStatus.Cancelled),
            (From.SuspendedBilling, TenantStatus.Active), (From.SuspendedBilling, TenantStatus.Suspended), (From.SuspendedBilling, TenantStatus.Cancelled),
            (From.Cancelled, TenantStatus.Active),
        };
        var data = new TheoryData<From, TenantStatus, bool>();
        foreach (var from in Enum.GetValues<From>())
        {
            foreach (var to in Enum.GetValues<TenantStatus>())
            {
                data.Add(from, to, allowed.Contains((from, to)));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void EveryCellOfTheTransitionGrid_IsTheDocumentedOne(From from, TenantStatus to, bool allowed)
    {
        var tenant = Start(from);
        var before = (tenant.Status, tenant.SuspensionCause);

        Assert.Equal(allowed, Tenant.CanChangeStatus(tenant.Status, tenant.SuspensionCause, to));
        var move = () =>
        {
            if (to == TenantStatus.Cancelled)
            {
                tenant.Cancel(Admin, null, null, Now, 30);
            }
            else
            {
                tenant.ChangeStatus(to, Admin, null, Now);
            }
        };

        if (allowed)
        {
            move();
            Assert.Equal(to == TenantStatus.Suspended ? SuspensionCause.Manual : null, tenant.SuspensionCause);
        }
        else
        {
            Assert.Throws<DomainException>(move);
            Assert.Equal(before, (tenant.Status, tenant.SuspensionCause));
        }
    }

    [Fact]
    public void AnAdministrator_CanTurnABillingSuspensionIntoAManualOne_SoAPaymentNoLongerLiftsIt()
    {
        var tenant = Start(From.SuspendedBilling);

        tenant.ChangeStatus(TenantStatus.Suspended, Admin, "Disputa del cobro", Now);

        Assert.Equal((TenantStatus.Suspended, SuspensionCause.Manual), (tenant.Status, tenant.SuspensionCause));
        Assert.Equal(TenantLifecycleOutcome.NotApplicable, tenant.ResumeFromBilling(ReasonCodes.BillingPaid, Now.AddDays(1)));
        Assert.Equal(SuspensionCause.Manual, tenant.SuspensionCause);
    }

    // ---- A late payment during a pause ------------------------------------------------------------------

    [Fact]
    public void ALatePaymentDuringAPause_IsRecordedAsAHold_AndTheCompanyStaysPaused()
    {
        var tenant = InStatus(TenantStatus.Paused);

        Assert.Equal(TenantLifecycleOutcome.Changed, tenant.SuspendForBilling(ReasonCodes.BillingPastDue, Now));

        Assert.Equal(TenantStatus.Paused, tenant.Status);
        Assert.Equal(Now, tenant.BillingHoldSince);
        Assert.Equal(TenantLifecycleOutcome.Unchanged, tenant.SuspendForBilling(ReasonCodes.BillingPastDue, Now.AddHours(1)));
        Assert.Equal(Now, tenant.BillingHoldSince);
    }

    [Fact]
    public void ResumingAPauseWithAnUnpaidHold_LandsInTheBillingSuspension()
    {
        var tenant = InStatus(TenantStatus.Paused);
        tenant.SuspendForBilling(ReasonCodes.BillingPastDue, Now);

        tenant.ChangeStatus(TenantStatus.Active, Admin, "Fin de la pausa", Now.AddDays(2));

        Assert.Equal((TenantStatus.Suspended, SuspensionCause.Billing), (tenant.Status, tenant.SuspensionCause));
        Assert.Null(tenant.BillingHoldSince);
        Assert.True(tenant.AcceptsWebhooks);
    }

    [Fact]
    public void APaymentDuringThePause_ClearsTheHold_AndTheResumeLandsInActive()
    {
        var tenant = InStatus(TenantStatus.Paused);
        tenant.SuspendForBilling(ReasonCodes.BillingPastDue, Now);

        Assert.Equal(TenantLifecycleOutcome.Changed, tenant.ResumeFromBilling(ReasonCodes.BillingPaid, Now.AddDays(1)));
        Assert.Equal(TenantStatus.Paused, tenant.Status);
        Assert.Null(tenant.BillingHoldSince);

        tenant.ChangeStatus(TenantStatus.Active, Admin, null, Now.AddDays(2));
        Assert.Equal(TenantStatus.Active, tenant.Status);
    }

    [Fact]
    public void CancellingClearsAHold()
    {
        var tenant = InStatus(TenantStatus.Paused);
        tenant.SuspendForBilling(ReasonCodes.BillingPastDue, Now);

        tenant.Cancel(Admin, null, null, Now.AddDays(1), 30);

        Assert.Null(tenant.BillingHoldSince);
    }

    // ---- The retention boundary --------------------------------------------------------------------------

    [Fact]
    public void TheDeletionIsDue_FromTheExactInstantTheWindowEnds()
    {
        var tenant = ActiveTenant();
        tenant.Cancel(Admin, null, null, Now, 30);
        var due = tenant.DataDeletionDueAt!.Value;

        Assert.True(tenant.IsDataDeletionDue(due));
        Assert.False(tenant.IsDataDeletionDue(due.AddTicks(-10))); // one microsecond before
    }
}
