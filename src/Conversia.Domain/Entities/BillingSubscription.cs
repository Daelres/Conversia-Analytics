using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// The subscription of one company (docs/BILLING.md): its plan, the current period (local dates, America/Bogota), the stored payment method
/// (only the gateway's id and a masked label, never card data) and where the collection stands. One per company. Billed in advance: the charge
/// of a period is created when it starts. No proration: a change of plan, lines or add-on is read when the next period's charge is created.
/// No permanence: it can be cancelled any time, effective at the end of the period, or immediately by the administrator.
/// </summary>
public class BillingSubscription : BaseEntity
{
    public const int MaxTrialDays = 90;
    public const int PaymentSourceIdMaxLength = 64;
    public const int PaymentMethodLabelMaxLength = 40;
    public const int EmailMaxLength = 320;

    public static readonly string TrialRuleMessage = $"El periodo gratuito debe ser un número entero de días entre 0 y {MaxTrialDays}.";

    public static readonly string BilledLinesRuleMessage = $"Las líneas facturadas deben ser un número entero entre 1 y {Tenant.MaxLinesLimit}.";

    public static readonly string AddOnQuantityRuleMessage = $"La cantidad de paquetes debe ser un número entero entre 0 y {BillingAddOn.MaxQuantity}.";

    public const string CancelledMessage = "La suscripción está cancelada.";

    public Guid TenantId { get; private set; }

    public Guid PlanId { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    /// <summary>The day of the month periods start on (see <see cref="BillingPeriods.EndOf"/>).</summary>
    public int AnchorDay { get; private set; }

    public DateOnly CurrentPeriodStart { get; private set; }

    /// <summary>Exclusive: the next period starts on this local date.</summary>
    public DateOnly CurrentPeriodEnd { get; private set; }

    /// <summary>When set, the administrator's number of billed lines instead of the company's line quota.</summary>
    public int? BilledLinesOverride { get; private set; }

    public Guid? AddOnId { get; private set; }

    public int AddOnQuantity { get; private set; }

    /// <summary>The subscription ends when the current period does (the owner or the administrator asked); can be undone until then.</summary>
    public bool CancelAtPeriodEnd { get; private set; }

    public DateTime? CancelledAt { get; private set; }

    public string? CancellationReasonCode { get; private set; }

    /// <summary>The gateway's id of the stored payment method (a payment source). Never card data.</summary>
    public string? PaymentSourceId { get; private set; }

    public PaymentMethodType? PaymentMethodType { get; private set; }

    /// <summary>What may be shown of the method: "VISA •••• 4242", "Nequi •••• 1111".</summary>
    public string? PaymentMethodLabel { get; private set; }

    /// <summary>The email the gateway receives with each charge (the owner's who registered the method).</summary>
    public string? BillingEmail { get; private set; }

    public DateTime? PaymentMethodUpdatedAt { get; private set; }

    /// <summary>When the billing job suspended the company for this subscription's unpaid charge; cleared once paid.</summary>
    public DateTime? BillingSuspendedAt { get; private set; }

    /// <summary>
    /// The local date the dunning of the open charge restarted from, when the administrator lifted a billing suspension while the charge was
    /// still unpaid: the grace period counts again from it (never from a stale date). Cleared once nothing is owed.
    /// </summary>
    public DateOnly? DunningRestartedOn { get; private set; }

    /// <summary>When the billing job must look at this subscription next (null: nothing to do until something changes).</summary>
    public DateTime? NextRunAt { get; private set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; private set; }

    private BillingSubscription()
    {
    }

    /// <summary>
    /// Starts the subscription on the local date <paramref name="today"/>. With a free period, the first period is the trial
    /// (<see cref="SubscriptionStatus.Trialing"/>) and the first charge comes when it ends; without one, the first period starts today and its
    /// charge is created by the next run of the billing job.
    /// </summary>
    public static BillingSubscription Start(Guid tenantId, Guid planId, DateOnly today, int trialDays, DateTime now)
    {
        if (trialDays is < 0 or > MaxTrialDays)
        {
            throw new DomainException(TrialRuleMessage);
        }

        var subscription = new BillingSubscription
        {
            TenantId = TextRules.RequiredId(tenantId, "La empresa es obligatoria."),
            PlanId = TextRules.RequiredId(planId, "El plan es obligatorio."),
            NextRunAt = UtcTime.Normalize(now),
        };

        if (trialDays > 0)
        {
            subscription.Status = SubscriptionStatus.Trialing;
            subscription.CurrentPeriodStart = today;
            subscription.CurrentPeriodEnd = today.AddDays(trialDays);
            subscription.AnchorDay = subscription.CurrentPeriodEnd.Day;
        }
        else
        {
            // No period yet: the job's first run starts it today (CurrentPeriodEnd == today means "a period starts now").
            subscription.Status = SubscriptionStatus.Active;
            subscription.CurrentPeriodStart = today;
            subscription.CurrentPeriodEnd = today;
            subscription.AnchorDay = today.Day;
        }

        return subscription;
    }

    public bool IsCancelled => Status == SubscriptionStatus.Cancelled;

    /// <summary>The lines a charge bills: the administrator's number, else the company's line quota (at least one).</summary>
    public int BilledLines(int tenantMaxActiveLines) => BilledLinesOverride ?? Math.Max(1, tenantMaxActiveLines);

    /// <summary>Starts the period that begins on <paramref name="start"/>. A period that does not follow the last one moves the anchor day to its start.</summary>
    public void StartPeriod(DateOnly start)
    {
        EnsureNotCancelled();
        if (start < CurrentPeriodEnd)
        {
            throw new DomainException("Un periodo no puede empezar antes de que termine el anterior.");
        }

        if (start != CurrentPeriodEnd)
        {
            AnchorDay = start.Day;
        }

        CurrentPeriodStart = start;
        CurrentPeriodEnd = BillingPeriods.EndOf(start, AnchorDay);
        if (Status == SubscriptionStatus.Trialing)
        {
            Status = SubscriptionStatus.Active;
        }
    }

    public void ChangePlan(Guid planId)
    {
        EnsureNotCancelled();
        PlanId = TextRules.RequiredId(planId, "El plan es obligatorio.");
    }

    public void SetBilledLinesOverride(int? lines)
    {
        EnsureNotCancelled();
        BilledLinesOverride = lines is null or (>= 1 and <= Tenant.MaxLinesLimit) ? lines : throw new DomainException(BilledLinesRuleMessage);
    }

    /// <summary>The add-on and quantity the next charges include (null or 0: none).</summary>
    public void SetAddOn(Guid? addOnId, int quantity)
    {
        EnsureNotCancelled();
        if (quantity is < 0 or > BillingAddOn.MaxQuantity)
        {
            throw new DomainException(AddOnQuantityRuleMessage);
        }

        AddOnId = quantity == 0 ? null : addOnId ?? throw new DomainException("Elige el paquete de conversaciones.");
        AddOnQuantity = AddOnId is null ? 0 : quantity;
    }

    /// <summary>Stores the gateway's payment source that replaces the previous one (which the gateway keeps; nothing of it is kept here).</summary>
    public void SetPaymentMethod(string paymentSourceId, PaymentMethodType type, string label, string email, DateTime now)
    {
        EnsureNotCancelled();
        PaymentSourceId = TextRules.Required(paymentSourceId, PaymentSourceIdMaxLength, "El identificador del medio de pago no es válido.");
        PaymentMethodType = Enum.IsDefined(type) ? type : throw new DomainException("El tipo de medio de pago no es válido.");
        PaymentMethodLabel = TextRules.Required(label, PaymentMethodLabelMaxLength, "La descripción del medio de pago no es válida.");
        BillingEmail = TextRules.Required(email, EmailMaxLength, "El correo de facturación no es válido.");
        PaymentMethodUpdatedAt = UtcTime.Normalize(now);
    }

    /// <summary>True when it changed.</summary>
    public bool SetCancelAtPeriodEnd(bool cancel)
    {
        EnsureNotCancelled();
        if (CancelAtPeriodEnd == cancel)
        {
            return false;
        }

        CancelAtPeriodEnd = cancel;
        return true;
    }

    /// <summary>Ends the subscription now. False when it had already ended.</summary>
    public bool Cancel(string reasonCode, DateTime now)
    {
        var code = ReasonCodes.Required(reasonCode);
        if (IsCancelled)
        {
            return false;
        }

        Status = SubscriptionStatus.Cancelled;
        CancelledAt = UtcTime.Normalize(now);
        CancellationReasonCode = code;
        CancelAtPeriodEnd = false;
        NextRunAt = null;
        return true;
    }

    public void MarkPastDue()
    {
        if (Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing)
        {
            Status = SubscriptionStatus.PastDue;
        }
    }

    /// <summary>Nothing is owed any more.</summary>
    public void MarkCurrent()
    {
        if (Status == SubscriptionStatus.PastDue)
        {
            Status = SubscriptionStatus.Active;
        }
    }

    public void RecordBillingSuspension(DateTime now) => BillingSuspendedAt ??= UtcTime.Normalize(now);

    public void ClearBillingSuspension() => BillingSuspendedAt = null;

    /// <summary>The administrator lifted the billing suspension while the charge is unpaid: the grace period starts again on <paramref name="today"/>.</summary>
    public void RestartDunning(DateOnly today)
    {
        BillingSuspendedAt = null;
        DunningRestartedOn = today;
    }

    /// <summary>The local date the grace period of a charge due on <paramref name="dueOn"/> counts from.</summary>
    public DateOnly DunningFrom(DateOnly dueOn) => DunningRestartedOn is { } restarted && restarted > dueOn ? restarted : dueOn;

    public void ClearDunningRestart() => DunningRestartedOn = null;

    public void Schedule(DateTime? nextRunAt) => NextRunAt = IsCancelled ? null : UtcTime.Normalize(nextRunAt);

    /// <summary>
    /// When the job must come back to ask about a pending attempt, even once cancelled: a payment still in flight can be approved, and that
    /// money must reach a person. Null when nothing is pending.
    /// </summary>
    public void SchedulePoll(DateTime? at) => NextRunAt = UtcTime.Normalize(at);

    private void EnsureNotCancelled()
    {
        if (IsCancelled)
        {
            throw new DomainException(CancelledMessage);
        }
    }
}
