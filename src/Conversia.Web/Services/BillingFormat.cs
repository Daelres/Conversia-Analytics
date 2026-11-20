using System.Globalization;
using Conversia.Domain.Enums;
using Conversia.Web.Components.Shared;

namespace Conversia.Web.Services;

/// <summary>How the billing pages word amounts, statuses and outcomes (neutral Spanish). Amounts arrive as COP in cents.</summary>
public static class BillingFormat
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-CO");

    /// <summary>"$ 180.000 COP".</summary>
    public static string Money(long cents) => "$ " + (cents / 100).ToString("N0", Culture) + " COP";

    public static string Pesos(long pesos) => "$ " + pesos.ToString("N0", Culture) + " COP";

    public static string Subscription(SubscriptionStatus? status) => status switch
    {
        SubscriptionStatus.Trialing => "Periodo gratuito",
        SubscriptionStatus.Active => "Al día",
        SubscriptionStatus.PastDue => "Pago pendiente",
        SubscriptionStatus.Cancelled => "Cancelada",
        _ => "Sin suscripción",
    };

    public static ChipTone SubscriptionTone(SubscriptionStatus? status) => status switch
    {
        SubscriptionStatus.Active or SubscriptionStatus.Trialing => ChipTone.Success,
        SubscriptionStatus.PastDue => ChipTone.Warning,
        SubscriptionStatus.Cancelled => ChipTone.Danger,
        _ => ChipTone.Neutral,
    };

    public static string Dunning(DunningStage? stage) => stage switch
    {
        DunningStage.Retrying => "En reintentos de cobro",
        DunningStage.Suspended => "Suspendida por pago pendiente",
        DunningStage.Cancelled => "Cancelada",
        _ => "Sin cobros vencidos",
    };

    public static string Charge(BillingChargeStatus status) => status switch
    {
        BillingChargeStatus.Open => "Pendiente",
        BillingChargeStatus.Paid => "Pagado",
        BillingChargeStatus.Failed => "No pagado",
        BillingChargeStatus.Void => "Anulado",
        _ => status.ToString(),
    };

    public static ChipTone ChargeTone(BillingChargeStatus status) => status switch
    {
        BillingChargeStatus.Paid => ChipTone.Success,
        BillingChargeStatus.Open => ChipTone.Warning,
        BillingChargeStatus.Failed => ChipTone.Danger,
        _ => ChipTone.Neutral,
    };

    public static string Attempt(PaymentAttemptStatus status) => status switch
    {
        PaymentAttemptStatus.Pending => "Esperando respuesta",
        PaymentAttemptStatus.Approved => "Aprobado",
        PaymentAttemptStatus.Declined => "Rechazado",
        PaymentAttemptStatus.Voided => "Anulado",
        PaymentAttemptStatus.Error => "Error",
        _ => status.ToString(),
    };

    public static string Kind(PaymentAttemptKind kind) => kind switch
    {
        PaymentAttemptKind.Automatic => "Cobro automático",
        PaymentAttemptKind.OwnerInitiated => "Pago manual",
        PaymentAttemptKind.PaymentLink => "Enlace de pago",
        _ => kind.ToString(),
    };

    public static string Settlement(ChargeSettlement? settlement) => settlement switch
    {
        ChargeSettlement.Gateway => "Pasarela de pagos",
        ChargeSettlement.Manual => "Registrado por el administrador",
        ChargeSettlement.Free => "Sin costo",
        _ => "",
    };

    /// <summary>Why a payment needs a person (docs/BILLING.md, "Review flags").</summary>
    public static string Review(string? reason) => reason switch
    {
        "duplicate_payment" => "Pago doble: el cobro ya estaba pagado",
        "paid_after_void" => "Pago sobre un cobro anulado",
        "amount_mismatch" => "Monto distinto al del cobro",
        "approved_after_resolution" => "Aprobado después de cerrado el intento",
        "voided_after_approval" => "Anulado en la pasarela después de aprobado",
        "unknown_outcome" => "Resultado desconocido: verifica en el panel de Wompi",
        "pending_too_long" => "Pendiente por más de un día",
        "reference_mismatch" => "Aprobado con una referencia que no es nuestra",
        "unmatched_payment" => "Pago aprobado que no corresponde a ningún cobro",
        "cancelled_after_payment" => "Empresa cancelada mientras llegaba un pago",
        null => "",
        _ => reason,
    };

    /// <summary>What the page says after "Pagar ahora".</summary>
    public static string PayNowOutcome(PaymentAttemptStatus status) => status switch
    {
        PaymentAttemptStatus.Approved => "Pago aprobado. ¡Gracias!",
        PaymentAttemptStatus.Pending => "El pago está en proceso. Si usas Nequi, apruébalo en tu aplicación; la página se actualiza cuando llegue la respuesta.",
        _ => "La pasarela rechazó el pago. Revisa tu medio de pago o usa el enlace de pago (PSE y otros medios).",
    };
}
