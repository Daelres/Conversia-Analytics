using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Billing;

/// <summary>
/// A company's subscription as its owner sees it (<c>GET /api/billing</c>) and, with <see cref="AdminBillingAccountResponse"/>, as the platform
/// administrator does. Amounts are COP in cents. The payment method is its masked label only.
/// </summary>
public class BillingAccountResponse
{
    public string PlanCode { get; set; } = string.Empty;

    public string PlanName { get; set; } = string.Empty;

    public long PricePerLineInCents { get; set; }

    public int IncludedAiConversations { get; set; }

    public SubscriptionStatus Status { get; set; }

    public DunningStage DunningStage { get; set; }

    public DateOnly CurrentPeriodStart { get; set; }

    public DateOnly CurrentPeriodEnd { get; set; }

    /// <summary>The lines the next charge bills.</summary>
    public int BilledLines { get; set; }

    public string? AddOnName { get; set; }

    public int AddOnQuantity { get; set; }

    public int AddOnConversations { get; set; }

    /// <summary>The date and amount of the next period's charge as things stand (no proration: changes apply from it). Null once cancelled.</summary>
    public DateOnly? NextChargeOn { get; set; }

    public long? NextChargeAmountInCents { get; set; }

    public bool CancelAtPeriodEnd { get; set; }

    public DateTime? CancelledAt { get; set; }

    public PaymentMethodType? PaymentMethodType { get; set; }

    /// <summary>"VISA •••• 4242", "Nequi •••• 1111".</summary>
    public string? PaymentMethodLabel { get; set; }

    public DateTime? BillingSuspendedAt { get; set; }

    /// <summary>False when the deployment has no gateway keys: the payment buttons are hidden.</summary>
    public bool GatewayConfigured { get; set; }

    /// <summary>The open charge (what is owed now), if any.</summary>
    public BillingChargeResponse? OpenCharge { get; set; }

    /// <summary>Newest first.</summary>
    public List<BillingChargeResponse> Charges { get; set; } = [];
}

public class AdminBillingAccountResponse : BillingAccountResponse
{
    public Guid TenantId { get; set; }

    public Guid PlanId { get; set; }

    public Guid? AddOnId { get; set; }

    public int? BilledLinesOverride { get; set; }

    public int TenantMaxActiveLines { get; set; }

    public DateTime? PaymentMethodUpdatedAt { get; set; }

    public DateTime? NextRunAt { get; set; }

    public string? CancellationReasonCode { get; set; }
}

public class BillingChargeResponse
{
    public Guid Id { get; set; }

    public DateOnly PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    public DateOnly DueOn { get; set; }

    public string PlanName { get; set; } = string.Empty;

    public int Lines { get; set; }

    public long UnitPriceInCents { get; set; }

    public string? AddOnName { get; set; }

    public int AddOnQuantity { get; set; }

    public long AmountInCents { get; set; }

    public string Currency { get; set; } = BillingMoney.Currency;

    public BillingChargeStatus Status { get; set; }

    public DateTime? PaidAt { get; set; }

    public ChargeSettlement? Settlement { get; set; }

    public string? Note { get; set; }

    public int AutomaticAttempts { get; set; }

    public DateTime? NextAttemptAt { get; set; }

    /// <summary>The payment link while it can still be used.</summary>
    public string? PaymentLinkUrl { get; set; }

    public bool HasPendingAttempt { get; set; }

    public List<BillingAttemptResponse> Attempts { get; set; } = [];
}

public class BillingAttemptResponse
{
    public Guid Id { get; set; }

    /// <summary>Why a person must look at it; null when nothing is pending review.</summary>
    public string? ReviewReason { get; set; }

    /// <summary>Our reference, to find the transaction in the gateway's dashboard.</summary>
    public string Reference { get; set; } = string.Empty;

    public DateTime AttemptedAt { get; set; }

    public PaymentAttemptKind Kind { get; set; }

    public PaymentAttemptStatus Status { get; set; }

    public PaymentMethodType? MethodType { get; set; }

    public string? StatusReason { get; set; }

    public DateTime? ResolvedAt { get; set; }
}

/// <summary>
/// What the browser needs to add a card or Nequi (<c>GET /api/billing/payment-method/setup</c>): the gateway's public key and URL, and the terms
/// to accept (their links and tokens). Never a secret.
/// </summary>
public class PaymentMethodSetupResponse
{
    public string PublicKey { get; set; } = string.Empty;

    public string ApiBaseUrl { get; set; } = string.Empty;

    public string AcceptanceToken { get; set; } = string.Empty;

    public string AcceptancePermalink { get; set; } = string.Empty;

    public string PersonalDataToken { get; set; } = string.Empty;

    public string PersonalDataPermalink { get; set; } = string.Empty;
}

/// <summary>
/// Body of <c>PUT /api/billing/payment-method</c>: the token the browser got from the gateway (card data never reaches us), the terms the payer
/// accepted, and, for a card, the brand and last four digits the gateway gave the browser (shown only, used when the gateway's source lacks them).
/// </summary>
public class SetPaymentMethodRequest
{
    public const int TokenMaxLength = 200;
    public const int AcceptanceTokenMaxLength = 4000;

    [Display(Name = "Tipo de medio de pago")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [EnumName(typeof(PaymentMethodType))]
    public string? Type { get; set; }

    [Display(Name = "Token del medio de pago")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [RegularExpression("^[A-Za-z0-9_\\-]{1,200}$", ErrorMessage = "El campo {0} no es válido.")]
    public string? Token { get; set; }

    [Display(Name = "Aceptación de términos")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [MaxLength(AcceptanceTokenMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    [RegularExpression("^[A-Za-z0-9_\\-\\.]+$", ErrorMessage = "El campo {0} no es válido.")]
    public string? AcceptanceToken { get; set; }

    [Display(Name = "Autorización de datos personales")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [MaxLength(AcceptanceTokenMaxLength, ErrorMessage = ValidationMessages.MaxLength)]
    [RegularExpression("^[A-Za-z0-9_\\-\\.]+$", ErrorMessage = "El campo {0} no es válido.")]
    public string? PersonalDataToken { get; set; }

    /// <summary>The payer ticked both terms. The gateway checks the tokens; this is the explicit consent of the page.</summary>
    [Display(Name = "Aceptación de términos")]
    [Range(typeof(bool), "true", "true", ErrorMessage = "Debes aceptar los términos y la autorización de datos personales.")]
    public bool AcceptedTerms { get; set; }

    [Display(Name = "Franquicia")]
    [RegularExpression("^[A-Za-z]{2,20}$", ErrorMessage = "El campo {0} no es válido.")]
    public string? CardBrand { get; set; }

    [Display(Name = "Últimos cuatro dígitos")]
    [RegularExpression("^[0-9]{4}$", ErrorMessage = "El campo {0} no es válido.")]
    public string? CardLastFour { get; set; }
}

public class PayNowResponse
{
    public PaymentAttemptStatus Status { get; set; }
}

public class PaymentLinkResponse
{
    public string Url { get; set; } = string.Empty;
}

// ---------------------------------------------------------------- administrator

/// <summary>Body of <c>POST /api/admin/billing/plans</c> and <c>PUT /api/admin/billing/plans/{id}</c> (the code is fixed once created).</summary>
public class PlanRequest
{
    [Display(Name = "Código")]
    [TrimmedText(BillingPlan.CodeMaxLength, Required = false)]
    public string? Code { get; set; }

    [Display(Name = "Nombre")]
    [TrimmedText(BillingPlan.NameMaxLength)]
    public string? Name { get; set; }

    [Display(Name = "Precio mensual por línea (COP)")]
    [Range(0, BillingMoney.MaxPriceInCents / 100, ErrorMessage = ValidationMessages.Range)]
    public long PricePerLinePesos { get; set; }

    [Display(Name = "Conversaciones con IA incluidas al mes")]
    [Range(0, Tenant.MaxAiMonthlyConversationQuota, ErrorMessage = ValidationMessages.Range)]
    public int IncludedAiConversations { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Body of <c>POST /api/admin/billing/plans/{id}/add-ons</c> and <c>PUT /api/admin/billing/add-ons/{id}</c>.</summary>
public class AddOnRequest
{
    [Display(Name = "Código")]
    [TrimmedText(BillingPlan.CodeMaxLength, Required = false)]
    public string? Code { get; set; }

    [Display(Name = "Nombre")]
    [TrimmedText(BillingPlan.NameMaxLength)]
    public string? Name { get; set; }

    [Display(Name = "Conversaciones con IA del paquete")]
    [Range(1, Tenant.MaxAiMonthlyConversationQuota, ErrorMessage = ValidationMessages.Range)]
    public int Conversations { get; set; }

    [Display(Name = "Precio del paquete (COP)")]
    [Range(0, BillingMoney.MaxPriceInCents / 100, ErrorMessage = ValidationMessages.Range)]
    public long PricePesos { get; set; }

    public bool IsActive { get; set; } = true;
}

public class PlanResponse
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long PricePerLinePesos { get; set; }

    public int IncludedAiConversations { get; set; }

    public bool IsActive { get; set; }

    public List<AddOnResponse> AddOns { get; set; } = [];
}

public class AddOnResponse
{
    public Guid Id { get; set; }

    public Guid PlanId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Conversations { get; set; }

    public long PricePesos { get; set; }

    public bool IsActive { get; set; }
}

/// <summary>Body of <c>POST /api/admin/tenants/{id}/billing/start</c>: activates the subscription the company asked for.</summary>
public class StartSubscriptionRequest
{
    [Display(Name = "Plan")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public Guid? PlanId { get; set; }

    [Display(Name = "Días gratis")]
    [Range(0, BillingSubscription.MaxTrialDays, ErrorMessage = ValidationMessages.Range)]
    public int TrialDays { get; set; }

    [Display(Name = "Líneas facturadas")]
    [Range(1, Tenant.MaxLinesLimit, ErrorMessage = ValidationMessages.Range)]
    public int? BilledLinesOverride { get; set; }
}

/// <summary>Body of <c>PUT /api/admin/tenants/{id}/billing</c>: plan, billed lines and add-on of the next charges (no proration).</summary>
public class ChangeSubscriptionRequest
{
    [Display(Name = "Plan")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public Guid? PlanId { get; set; }

    [Display(Name = "Líneas facturadas")]
    [Range(1, Tenant.MaxLinesLimit, ErrorMessage = ValidationMessages.Range)]
    public int? BilledLinesOverride { get; set; }

    public Guid? AddOnId { get; set; }

    [Display(Name = "Cantidad de paquetes")]
    [Range(0, BillingAddOn.MaxQuantity, ErrorMessage = ValidationMessages.Range)]
    public int AddOnQuantity { get; set; }
}

/// <summary>Body of the administrator's charge actions (mark paid, void): why, kept in the audit trail.</summary>
public class ChargeActionRequest
{
    [Display(Name = "Motivo")]
    [TrimmedText(BillingCharge.NoteMaxLength)]
    public string? Reason { get; set; }
}

/// <summary>
/// Body of <c>POST /api/admin/tenants/{id}/billing/attempts/{attemptId}/review</c>: the administrator's verdict on an attempt flagged for
/// review, after checking the gateway's dashboard. <see cref="MarkFailed"/> closes a pending attempt as failed (no transaction exists).
/// </summary>
public class ReviewAttemptRequest
{
    public bool MarkFailed { get; set; }

    [Display(Name = "Motivo")]
    [TrimmedText(BillingCharge.NoteMaxLength)]
    public string? Reason { get; set; }
}

/// <summary>Body of <c>POST /api/admin/tenants/{id}/billing/cancel</c>.</summary>
public class AdminCancelSubscriptionRequest
{
    /// <summary>True: at the end of the period (undoable); false: now (unpaid charges voided).</summary>
    public bool AtPeriodEnd { get; set; } = true;

    [Display(Name = "Motivo")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public string? ReasonCode { get; set; }
}

/// <summary>A review item (docs/BILLING.md, "Review flags"): money a person must look at. Ids, amounts and the reason only.</summary>
public class BillingReviewItemResponse
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public Guid? ChargeId { get; set; }

    public Guid? AttemptId { get; set; }

    public string? TransactionId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public long? AmountInCents { get; set; }

    public string? Currency { get; set; }

    public DateTime FlaggedAt { get; set; }
}

/// <summary>Body of <c>POST /api/admin/billing/review-items/{id}/review</c>: what the administrator did about it.</summary>
public class ReviewItemRequest
{
    [Display(Name = "Motivo")]
    [TrimmedText(BillingCharge.NoteMaxLength)]
    public string? Reason { get; set; }
}
