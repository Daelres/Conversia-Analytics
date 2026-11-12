namespace Conversia.Application.Exceptions;

/// <summary>Why a billing request could not be done. Each one is a distinct answer of the API (docs/BILLING.md, "API").</summary>
public enum BillingProblem
{
    /// <summary>404: the company has no subscription.</summary>
    SubscriptionNotFound,

    /// <summary>404.</summary>
    PlanNotFound,

    /// <summary>404.</summary>
    AddOnNotFound,

    /// <summary>404: no such charge in the caller's company.</summary>
    ChargeNotFound,

    /// <summary>409: the company already has a subscription.</summary>
    SubscriptionExists,

    /// <summary>409: the subscription has ended.</summary>
    SubscriptionCancelled,

    /// <summary>409: another plan already has that code (or the plan another add-on with it).</summary>
    DuplicateCode,

    /// <summary>409: the plan or add-on is deactivated and cannot be newly chosen.</summary>
    PlanInactive,

    /// <summary>409: the add-on belongs to another plan.</summary>
    AddOnOfAnotherPlan,

    /// <summary>409: the charge is not open (paid, voided, closed).</summary>
    ChargeNotOpen,

    /// <summary>409: an attempt of the charge is still waiting for its answer: no second one starts (no double charge).</summary>
    AttemptPending,

    /// <summary>409: there is no stored payment method.</summary>
    NoPaymentMethod,

    /// <summary>422: the gateway refused the payment method (an invalid or unapproved token, a source it could not create).</summary>
    PaymentMethodRejected,

    /// <summary>503: the gateway could not be reached or failed. Nothing is assumed about what it did.</summary>
    GatewayUnavailable,

    /// <summary>503: the gateway's keys are not configured in this deployment.</summary>
    GatewayNotConfigured,
}

/// <summary>A billing request that cannot be done. The API maps each <see cref="Problem"/> to its status and puts <see cref="Code"/> in the body.</summary>
public class BillingException : Exception
{
    public BillingException(BillingProblem problem, Exception? innerException = null)
        : base(MessageOf(problem), innerException)
    {
        Problem = problem;
    }

    public BillingProblem Problem { get; }

    public string Code => CodeOf(Problem);

    public static string CodeOf(BillingProblem problem) => problem switch
    {
        BillingProblem.SubscriptionNotFound => "subscription_not_found",
        BillingProblem.PlanNotFound => "plan_not_found",
        BillingProblem.AddOnNotFound => "addon_not_found",
        BillingProblem.ChargeNotFound => "charge_not_found",
        BillingProblem.SubscriptionExists => "subscription_exists",
        BillingProblem.SubscriptionCancelled => "subscription_cancelled",
        BillingProblem.DuplicateCode => "billing_code_taken",
        BillingProblem.PlanInactive => "plan_inactive",
        BillingProblem.AddOnOfAnotherPlan => "addon_of_another_plan",
        BillingProblem.ChargeNotOpen => "charge_not_open",
        BillingProblem.AttemptPending => "payment_attempt_pending",
        BillingProblem.NoPaymentMethod => "payment_method_missing",
        BillingProblem.PaymentMethodRejected => "payment_method_rejected",
        BillingProblem.GatewayUnavailable => "billing_gateway_unavailable",
        BillingProblem.GatewayNotConfigured => "billing_not_configured",
        _ => "billing_problem",
    };

    public int StatusCode => Problem switch
    {
        BillingProblem.SubscriptionNotFound or BillingProblem.PlanNotFound or BillingProblem.AddOnNotFound or BillingProblem.ChargeNotFound => 404,
        BillingProblem.PaymentMethodRejected => 422,
        BillingProblem.GatewayUnavailable or BillingProblem.GatewayNotConfigured => 503,
        _ => 409,
    };

    private static string MessageOf(BillingProblem problem) => problem switch
    {
        BillingProblem.SubscriptionNotFound => "La empresa no tiene una suscripción.",
        BillingProblem.PlanNotFound => "Plan no encontrado.",
        BillingProblem.AddOnNotFound => "Paquete de conversaciones no encontrado.",
        BillingProblem.ChargeNotFound => "Cobro no encontrado.",
        BillingProblem.SubscriptionExists => "La empresa ya tiene una suscripción.",
        BillingProblem.SubscriptionCancelled => "La suscripción está cancelada.",
        BillingProblem.DuplicateCode => "Ya existe un plan o paquete con ese código.",
        BillingProblem.PlanInactive => "El plan o el paquete está desactivado y no se puede elegir.",
        BillingProblem.AddOnOfAnotherPlan => "El paquete de conversaciones pertenece a otro plan.",
        BillingProblem.ChargeNotOpen => "El cobro ya no está pendiente de pago.",
        BillingProblem.AttemptPending => "Hay un pago de este cobro esperando respuesta. Espera a que termine antes de intentarlo de nuevo.",
        BillingProblem.NoPaymentMethod => "No hay un medio de pago registrado. Agrega una tarjeta o Nequi.",
        BillingProblem.PaymentMethodRejected => "La pasarela de pagos no aceptó el medio de pago. Revisa los datos e inténtalo de nuevo.",
        BillingProblem.GatewayUnavailable => "La pasarela de pagos no respondió. Inténtalo de nuevo en unos minutos.",
        BillingProblem.GatewayNotConfigured => "Los pagos en línea no están configurados en este entorno.",
        _ => "No se pudo completar la operación de facturación.",
    };
}
