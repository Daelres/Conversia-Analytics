using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// What a company owes for one period (docs/BILLING.md, "Charges"). Not a tax invoice: electronic invoicing is out of scope. The plan, the
/// lines and the add-on are copied when the charge is created, so a later change of the catalogue never changes what was charged. One per
/// period of a subscription (a unique index on <c>(SubscriptionId, PeriodStart)</c>), so a period can never be charged twice.
/// </summary>
public class BillingCharge : BaseEntity
{
    public const int NoteMaxLength = 200;
    public const int PaymentLinkIdMaxLength = 64;
    public const int PaymentLinkUrlMaxLength = 300;

    public static readonly string NoteRuleMessage = $"El motivo {TextRules.RuleText(NoteMaxLength)}.";

    public Guid TenantId { get; private set; }

    public Guid SubscriptionId { get; private set; }

    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    /// <summary>The local date it is due: the first day of its period (billed in advance).</summary>
    public DateOnly DueOn { get; private set; }

    public string PlanCode { get; private set; } = string.Empty;

    public string PlanName { get; private set; } = string.Empty;

    public int Lines { get; private set; }

    public long UnitPriceInCents { get; private set; }

    public string? AddOnName { get; private set; }

    public int AddOnQuantity { get; private set; }

    /// <summary>The AI conversations one package adds.</summary>
    public int AddOnConversations { get; private set; }

    public long AddOnUnitPriceInCents { get; private set; }

    public long AmountInCents { get; private set; }

    public string Currency { get; private set; } = BillingMoney.Currency;

    public BillingChargeStatus Status { get; private set; } = BillingChargeStatus.Open;

    public DateTime? PaidAt { get; private set; }

    public ChargeSettlement? Settlement { get; private set; }

    /// <summary>Why the administrator settled or voided it (a bank transfer...). Never card data.</summary>
    public string? Note { get; private set; }

    public DateTime? ClosedAt { get; private set; }

    /// <summary>The automatic attempts started so far (the first on the due date, then one per retry day).</summary>
    public int AutomaticAttempts { get; private set; }

    /// <summary>When the next automatic attempt is due; null while one is pending or when none is left.</summary>
    public DateTime? NextAttemptAt { get; private set; }

    /// <summary>The gateway's single-use payment link of this charge, for PSE and the other manual methods.</summary>
    public string? PaymentLinkId { get; private set; }

    public string? PaymentLinkUrl { get; private set; }

    public DateTime? PaymentLinkExpiresAt { get; private set; }

    public uint Version { get; private set; }

    private BillingCharge()
    {
    }

    public static BillingCharge Create(
        BillingSubscription subscription, BillingPlan plan, int lines, BillingAddOn? addOn, int addOnQuantity, DateOnly periodStart, DateOnly periodEnd,
        DateTime firstAttemptAt)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(plan);
        if (lines is < 1 or > Tenant.MaxLinesLimit)
        {
            throw new DomainException(BillingSubscription.BilledLinesRuleMessage);
        }

        if (periodEnd <= periodStart)
        {
            throw new DomainException("El periodo del cobro no es válido.");
        }

        var quantity = addOn is null ? 0 : addOnQuantity;
        if (quantity is < 0 or > BillingAddOn.MaxQuantity)
        {
            throw new DomainException(BillingSubscription.AddOnQuantityRuleMessage);
        }

        var charge = new BillingCharge
        {
            TenantId = subscription.TenantId,
            SubscriptionId = subscription.Id,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            DueOn = periodStart,
            PlanCode = plan.Code,
            PlanName = plan.Name,
            Lines = lines,
            UnitPriceInCents = plan.PricePerLineInCents,
            AddOnName = quantity > 0 ? addOn!.Name : null,
            AddOnQuantity = quantity,
            AddOnConversations = quantity > 0 ? addOn!.Conversations : 0,
            AddOnUnitPriceInCents = quantity > 0 ? addOn!.PriceInCents : 0,
        };
        charge.AmountInCents = checked(charge.Lines * charge.UnitPriceInCents + charge.AddOnQuantity * charge.AddOnUnitPriceInCents);
        charge.NextAttemptAt = UtcTime.Normalize(firstAttemptAt);
        return charge;
    }

    /// <summary>The AI conversations this charge adds to the quota of its period once paid.</summary>
    public int AddedAiConversations => AddOnQuantity * AddOnConversations;

    public bool IsOpen => Status == BillingChargeStatus.Open;

    /// <summary>Settles it. False (nothing changes) when it was already paid. A voided charge cannot be paid.</summary>
    public bool MarkPaid(ChargeSettlement settlement, string? note, DateTime now)
    {
        if (Status == BillingChargeStatus.Paid)
        {
            return false;
        }

        if (Status == BillingChargeStatus.Void)
        {
            throw new DomainException("Un cobro anulado no se puede marcar como pagado.");
        }

        Note = settlement == ChargeSettlement.Manual ? TextRules.Required(note, NoteMaxLength, NoteRuleMessage) : null;
        Status = BillingChargeStatus.Paid;
        Settlement = settlement;
        PaidAt = UtcTime.Normalize(now);
        ClosedAt = PaidAt;
        NextAttemptAt = null;
        return true;
    }

    /// <summary>Annuls it: nothing is owed. Only an unpaid charge.</summary>
    public void Void(string? reason, DateTime now)
    {
        if (Status is BillingChargeStatus.Paid or BillingChargeStatus.Void)
        {
            throw new DomainException("Solo se puede anular un cobro sin pagar.");
        }

        Note = TextRules.Required(reason, NoteMaxLength, NoteRuleMessage);
        Status = BillingChargeStatus.Void;
        ClosedAt = UtcTime.Normalize(now);
        NextAttemptAt = null;
    }

    /// <summary>Closes it unpaid because the subscription ended for non-payment.</summary>
    public void Fail(DateTime now)
    {
        if (Status != BillingChargeStatus.Open)
        {
            return;
        }

        Status = BillingChargeStatus.Failed;
        ClosedAt = UtcTime.Normalize(now);
        NextAttemptAt = null;
    }

    /// <summary>An automatic attempt starts: counted now, and no other is scheduled until it ends.</summary>
    public void BeginAutomaticAttempt()
    {
        EnsureOpen();
        AutomaticAttempts++;
        NextAttemptAt = null;
    }

    /// <summary>When the next automatic attempt is due (null: no more).</summary>
    public void ScheduleNextAttempt(DateTime? at) => NextAttemptAt = IsOpen ? UtcTime.Normalize(at) : null;

    public void SetPaymentLink(string id, string url, DateTime expiresAt)
    {
        EnsureOpen();
        PaymentLinkId = TextRules.Required(id, PaymentLinkIdMaxLength, "El identificador del enlace de pago no es válido.");
        PaymentLinkUrl = TextRules.Required(url, PaymentLinkUrlMaxLength, "La dirección del enlace de pago no es válida.");
        PaymentLinkExpiresAt = UtcTime.Normalize(expiresAt);
    }

    public bool HasUsablePaymentLink(DateTime now) => PaymentLinkId is not null && PaymentLinkExpiresAt > now;

    private void EnsureOpen()
    {
        if (!IsOpen)
        {
            throw new DomainException("El cobro ya no está pendiente de pago.");
        }
    }
}

/// <summary>
/// One try to collect a charge (docs/BILLING.md, "Attempts"). Idempotent twice over: by our <see cref="Reference"/> (unique, sent to the gateway)
/// and by the gateway's <see cref="GatewayTransactionId"/> (unique once known), so a replayed event or a second poll never counts a payment twice.
/// </summary>
public class BillingPaymentAttempt : BaseEntity
{
    public const int TransactionIdMaxLength = 64;
    public const int ReferenceMaxLength = 255;
    public const int StatusReasonMaxLength = 200;
    public const int ReviewReasonMaxLength = 64;

    public Guid TenantId { get; private set; }

    public Guid ChargeId { get; private set; }

    /// <summary>Ours (<see cref="BillingReference"/>) for the attempts we start; the gateway's for a payment through the link.</summary>
    public string Reference { get; private set; } = string.Empty;

    public string? GatewayTransactionId { get; private set; }

    public PaymentAttemptKind Kind { get; private set; }

    public PaymentAttemptStatus Status { get; private set; } = PaymentAttemptStatus.Pending;

    public long AmountInCents { get; private set; }

    public string Currency { get; private set; } = BillingMoney.Currency;

    public PaymentMethodType? MethodType { get; private set; }

    /// <summary>The gateway's reason for a decline or an error (or ours: <c>amount_mismatch</c>, <c>no_transaction</c>), cut to a short text.</summary>
    public string? StatusReason { get; private set; }

    public DateTime AttemptedAt { get; private set; }

    public DateTime? ResolvedAt { get; private set; }

    /// <summary>The last time the gateway was asked about it while pending.</summary>
    public DateTime? LastCheckedAt { get; private set; }

    /// <summary>How many times the gateway was asked about it while pending (the lookups of an unknown outcome back off with it).</summary>
    public int Checks { get; private set; }

    /// <summary>
    /// Why a person must look at it (<c>duplicate_payment</c>, <c>approved_after_resolution</c>, <c>unknown_outcome</c>...): money may have
    /// moved that the charge does not reflect. Shown to the administrator and counted by the monitoring until reviewed.
    /// </summary>
    public string? ReviewReason { get; private set; }

    public DateTime? ReviewFlaggedAt { get; private set; }

    public DateTime? ReviewedAt { get; private set; }

    public bool NeedsReview => ReviewReason is not null && ReviewedAt is null;

    private BillingPaymentAttempt()
    {
    }

    /// <summary>An attempt we start against the stored payment method.</summary>
    public static BillingPaymentAttempt Begin(BillingCharge charge, PaymentAttemptKind kind, PaymentMethodType? methodType, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(charge);
        if (!charge.IsOpen)
        {
            throw new DomainException("El cobro ya no está pendiente de pago.");
        }

        if (kind == PaymentAttemptKind.PaymentLink)
        {
            throw new DomainException("Un pago por enlace lo registra la pasarela.");
        }

        return new BillingPaymentAttempt
        {
            TenantId = charge.TenantId,
            ChargeId = charge.Id,
            Reference = BillingReference.New(),
            Kind = kind,
            AmountInCents = charge.AmountInCents,
            Currency = charge.Currency,
            MethodType = methodType,
            AttemptedAt = UtcTime.Normalize(now),
        };
    }

    /// <summary>A payment the gateway reports through the charge's payment link (the payer chose the method there).</summary>
    public static BillingPaymentAttempt FromPaymentLink(BillingCharge charge, string transactionId, string? reference, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(charge);
        var id = TextRules.Required(transactionId, TransactionIdMaxLength, "El identificador de la transacción no es válido.");
        return new BillingPaymentAttempt
        {
            TenantId = charge.TenantId,
            ChargeId = charge.Id,
            Reference = TextRules.Optional(reference, ReferenceMaxLength, "La referencia de la transacción no es válida.") ?? "link_" + id,
            GatewayTransactionId = id,
            Kind = PaymentAttemptKind.PaymentLink,
            AmountInCents = charge.AmountInCents,
            Currency = charge.Currency,
            AttemptedAt = UtcTime.Normalize(now),
        };
    }

    public bool IsPending => Status == PaymentAttemptStatus.Pending;

    /// <summary>The gateway created the transaction. A different id for the same attempt is refused (it would be another payment).</summary>
    public void AttachTransaction(string transactionId)
    {
        var id = TextRules.Required(transactionId, TransactionIdMaxLength, "El identificador de la transacción no es válido.");
        if (GatewayTransactionId is not null && GatewayTransactionId != id)
        {
            throw new DomainException("El intento ya tiene otra transacción.");
        }

        GatewayTransactionId = id;
    }

    /// <summary>The final answer. False (nothing changes) when the attempt was already resolved, or the answer is still pending.</summary>
    public bool Resolve(PaymentAttemptStatus status, string? reason, DateTime now)
    {
        if (!IsPending || status == PaymentAttemptStatus.Pending || !Enum.IsDefined(status))
        {
            return false;
        }

        Status = status;
        StatusReason = string.IsNullOrWhiteSpace(reason) ? null : TextRules.Truncate(reason.Trim().Replace('\0', ' '), StatusReasonMaxLength);
        ResolvedAt = UtcTime.Normalize(now);
        return true;
    }

    public void MarkChecked(DateTime now)
    {
        LastCheckedAt = UtcTime.Normalize(now);
        Checks++;
    }

    /// <summary>Flags it for a person. False when it is already waiting for a review (one flag at a time, so a replay never piles them up).</summary>
    public bool FlagForReview(string reason, DateTime now)
    {
        if (NeedsReview)
        {
            return false;
        }

        ReviewReason = TextRules.Truncate(TextRules.Required(reason, ReviewReasonMaxLength, "El motivo de revisión no es válido."), ReviewReasonMaxLength);
        ReviewFlaggedAt = UtcTime.Normalize(now);
        ReviewedAt = null;
        return true;
    }

    /// <summary>The administrator looked at it (and did what was needed in the gateway's dashboard).</summary>
    public bool MarkReviewed(DateTime now)
    {
        if (!NeedsReview)
        {
            return false;
        }

        ReviewedAt = UtcTime.Normalize(now);
        return true;
    }
}

/// <summary>
/// One event the gateway sent to <c>POST /api/billing/wompi/events</c>, stored once its signature was verified and processed later by the
/// billing job (like <see cref="WebhookEvent"/>). Keeps ids only: the transaction is always re-read from the gateway, so neither the payload's
/// amount nor its status is ever trusted, and no customer data is stored. The body hash is unique, so a re-sent event stores nothing new.
/// Platform-level: an event names no company until its transaction is matched.
/// </summary>
public class BillingEvent : BaseEntity
{
    public const int EventTypeMaxLength = 64;
    public const int LastErrorMaxLength = 500;

    public byte[] BodyHash { get; private set; } = [];

    public string EventType { get; private set; } = string.Empty;

    public string? TransactionId { get; private set; }

    /// <summary>The payment link the transaction came from, as the event says (a hint for matching; the amount is checked against the gateway).</summary>
    public string? PaymentLinkId { get; private set; }

    public DateTime ReceivedAt { get; private set; }

    public BillingEventStatus Status { get; private set; } = BillingEventStatus.Pending;

    public int Attempts { get; private set; }

    public DateTime NextAttemptAt { get; private set; }

    public DateTime? ProcessedAt { get; private set; }

    public string? LastError { get; private set; }

    private BillingEvent()
    {
    }

    public static BillingEvent Receive(byte[] bodyHash, string eventType, string? transactionId, string? paymentLinkId, DateTime now)
    {
        if (bodyHash is not { Length: WebhookEvent.BodyHashLength })
        {
            throw new DomainException($"El hash del cuerpo debe tener {WebhookEvent.BodyHashLength} bytes.");
        }

        var received = UtcTime.Normalize(now);
        return new BillingEvent
        {
            BodyHash = bodyHash,
            EventType = TextRules.Required(eventType, EventTypeMaxLength, "El tipo de evento no es válido."),
            TransactionId = TextRules.Optional(transactionId, BillingPaymentAttempt.TransactionIdMaxLength, "El identificador de la transacción no es válido."),
            PaymentLinkId = TextRules.Optional(paymentLinkId, BillingCharge.PaymentLinkIdMaxLength, "El identificador del enlace de pago no es válido."),
            ReceivedAt = received,
            NextAttemptAt = received,
        };
    }

    public void BeginAttempt(DateTime now, TimeSpan lease)
    {
        Attempts++;
        NextAttemptAt = UtcTime.Normalize(now) + lease;
    }

    public void Complete(BillingEventStatus status, DateTime now)
    {
        Status = status is BillingEventStatus.Processed or BillingEventStatus.Ignored ? status : throw new DomainException("Estado de evento no válido.");
        ProcessedAt = UtcTime.Normalize(now);
        LastError = null;
    }

    public void RegisterFailure(string sanitizedError, DateTime now, int maxAttempts, TimeSpan baseDelay)
    {
        LastError = TextRules.Truncate(sanitizedError, LastErrorMaxLength);
        if (Attempts >= maxAttempts)
        {
            Status = BillingEventStatus.Failed;
            return;
        }

        NextAttemptAt = UtcTime.Normalize(now) + WebhookEvent.BackoffDelay(Attempts, baseDelay);
    }
}

/// <summary>
/// Extra AI conversations a paid charge's add-on gives the company for the period it paid (<c>[ValidFrom, ValidUntil)</c>, UTC). The AI quota of
/// a company at an instant is its base quota plus the grants valid then (docs/BILLING.md, "AI add-ons"). One per charge.
/// </summary>
public class AiQuotaGrant : BaseEntity
{
    public Guid TenantId { get; private set; }

    public Guid ChargeId { get; private set; }

    public int Conversations { get; private set; }

    public DateTime ValidFrom { get; private set; }

    public DateTime ValidUntil { get; private set; }

    private AiQuotaGrant()
    {
    }

    public static AiQuotaGrant For(BillingCharge charge, DateTime validFrom, DateTime validUntil)
    {
        ArgumentNullException.ThrowIfNull(charge);
        if (charge.AddedAiConversations <= 0 || validUntil <= validFrom)
        {
            throw new DomainException("El cobro no incluye conversaciones adicionales.");
        }

        return new AiQuotaGrant
        {
            TenantId = charge.TenantId,
            ChargeId = charge.Id,
            Conversations = charge.AddedAiConversations,
            ValidFrom = UtcTime.Normalize(validFrom),
            ValidUntil = UtcTime.Normalize(validUntil),
        };
    }
}

/// <summary>
/// Money a person must look at that no attempt can carry (docs/BILLING.md, "Review flags"): an approved transaction that matches no attempt
/// or charge, an approval whose reference is not ours, a company cancelled while a payment landed. Durable (never purged with events) and
/// platform-level: an unmatched payment names no company. Counted by the monitoring until the administrator marks it reviewed.
/// </summary>
public class BillingReviewItem : BaseEntity
{
    public Guid? TenantId { get; private set; }

    public Guid? ChargeId { get; private set; }

    public Guid? AttemptId { get; private set; }

    public string? TransactionId { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public long? AmountInCents { get; private set; }

    public string? Currency { get; private set; }

    public DateTime FlaggedAt { get; private set; }

    public DateTime? ReviewedAt { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    public string? ReviewNote { get; private set; }

    public bool IsOpen => ReviewedAt is null;

    private BillingReviewItem()
    {
    }

    public static BillingReviewItem Flag(
        string reason, DateTime now, Guid? tenantId = null, Guid? chargeId = null, Guid? attemptId = null, string? transactionId = null,
        long? amountInCents = null, string? currency = null)
        => new()
        {
            Reason = TextRules.Required(reason, BillingPaymentAttempt.ReviewReasonMaxLength, "El motivo de revisión no es válido."),
            FlaggedAt = UtcTime.Normalize(now),
            TenantId = tenantId,
            ChargeId = chargeId,
            AttemptId = attemptId,
            TransactionId = TextRules.Optional(transactionId, BillingPaymentAttempt.TransactionIdMaxLength, "El identificador de la transacción no es válido."),
            AmountInCents = amountInCents,
            Currency = currency is null ? null : TextRules.Truncate(currency, 3),
        };

    public bool MarkReviewed(Guid adminId, string note, DateTime now)
    {
        if (!IsOpen)
        {
            return false;
        }

        ReviewedAt = UtcTime.Normalize(now);
        ReviewedBy = TextRules.RequiredId(adminId, "El administrador es obligatorio.");
        ReviewNote = TextRules.Required(note, BillingCharge.NoteMaxLength, BillingCharge.NoteRuleMessage);
        return true;
    }
}
