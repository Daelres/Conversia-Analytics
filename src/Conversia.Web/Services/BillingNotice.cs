namespace Conversia.Web.Services;

/// <summary>Outcomes of the billing page, carried across the post/redirect/get as fixed codes, and the words of the API's billing refusals.</summary>
public static class BillingNotice
{
    public const string Paid = "paid";
    public const string Pending = "pending";
    public const string Declined = "declined";
    public const string MethodSaved = "method";
    public const string CancelScheduled = "cancel";
    public const string CancelUndone = "undo";

    public const string Path = "/organizacion/facturacion";

    public static string Url(string code) => $"{Path}?notice={code}";

    public static string? Describe(string? code) => code switch
    {
        Paid => BillingFormat.PayNowOutcome(Conversia.Domain.Enums.PaymentAttemptStatus.Approved),
        Pending => BillingFormat.PayNowOutcome(Conversia.Domain.Enums.PaymentAttemptStatus.Pending),
        Declined => BillingFormat.PayNowOutcome(Conversia.Domain.Enums.PaymentAttemptStatus.Declined),
        MethodSaved => "Medio de pago guardado. Si hay un cobro pendiente, se intenta en los próximos minutos.",
        CancelScheduled => "La suscripción terminará al final del periodo actual.",
        CancelUndone => "La suscripción sigue activa.",
        _ => null,
    };

    /// <summary>The page's words for a billing refusal the API named; null for any other outcome (the generic handling applies).</summary>
    public static string? Problem(string? code) => code switch
    {
        "payment_attempt_pending" => "Hay un pago de este cobro esperando respuesta. Espera a que termine antes de intentarlo de nuevo.",
        "payment_method_missing" => "No hay un medio de pago registrado. Agrega una tarjeta o Nequi.",
        "payment_method_rejected" => "La pasarela de pagos no aceptó el medio de pago. Revisa los datos e inténtalo de nuevo.",
        "charge_not_open" => "Ese cobro ya no está pendiente de pago.",
        "subscription_cancelled" => "La suscripción está cancelada.",
        "billing_gateway_unavailable" => "La pasarela de pagos no respondió. Inténtalo de nuevo en unos minutos.",
        "billing_not_configured" => "Los pagos en línea no están disponibles en este momento. Contacta al equipo de Conversia.",
        "subscription_exists" => "La empresa ya tiene una suscripción.",
        "billing_code_taken" => "Ya existe un plan o paquete con ese código.",
        "plan_inactive" => "El plan o el paquete está desactivado.",
        "addon_of_another_plan" => "El paquete pertenece a otro plan.",
        _ => null,
    };
}
