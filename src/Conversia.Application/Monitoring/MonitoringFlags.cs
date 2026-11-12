namespace Conversia.Application.Monitoring;

/// <summary>
/// The thresholds of the overview's flags (<c>PlatformMonitoring:*</c>, docs/PLATFORM_ADMIN.md, "Monitoring"). A flag is raised at the threshold
/// (greater than or equal). Rates need a minimum volume so a handful of events does not raise them.
/// </summary>
public sealed record MonitoringThresholds(
    int WebhookBacklog,
    int WebhookOldestPendingMinutes,
    int ClassificationBacklog,
    int ClassificationOldestMinutes,
    int FailedDeliveryRatePercent,
    int MinDeliveriesForRate,
    int SilentLineHours,
    int ErrorsLast24h,
    int SendFailureRatePercent,
    int MinSendsForRate,
    int OverviewCacheSeconds = 20)
{
    public const string SectionName = "PlatformMonitoring";

    public static MonitoringThresholds Default { get; } = new(
        WebhookBacklog: 500, WebhookOldestPendingMinutes: 10, ClassificationBacklog: 500, ClassificationOldestMinutes: 60, FailedDeliveryRatePercent: 5,
        MinDeliveriesForRate: 20, SilentLineHours: 24, ErrorsLast24h: 20, SendFailureRatePercent: 20, MinSendsForRate: 10, OverviewCacheSeconds: 20);
}

/// <summary>What the flags are computed from (platform-wide).</summary>
/// <param name="HealthStatus"><c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>.</param>
/// <param name="FailedSends7d">Sends of the last 7 days that did not end <c>sent</c>.</param>
/// <param name="BreakerState">The LLM circuit breaker of this instance; null when the LLM is not in use.</param>
public sealed record MonitoringFlagInput(
    string HealthStatus,
    int WebhookPending,
    TimeSpan? OldestPending,
    int ClassificationWaiting,
    TimeSpan? OldestClassification,
    int ClassificationStopped,
    int Deliveries24h,
    int FailedDeliveries24h,
    int Errors24h,
    int Sends7d,
    int FailedSends7d,
    int SilentCompanies,
    int AiLimitedCompanies,
    bool DeploymentCapReached,
    string? BreakerState,
    long LostUsageRecords = 0,
    int StuckDeletions = 0,
    int BillingEventsFailed = 0,
    int BillingAttemptsStuck = 0,
    int BillingNeedsReview = 0);

/// <param name="Code">Stable machine code (<c>webhook_backlog</c>, ...).</param>
/// <param name="Severity"><c>warning</c> or <c>critical</c>.</param>
/// <param name="Message">What it means, in Spanish, with the numbers.</param>
public sealed record MonitoringFlag(string Code, string Severity, string Message);

public static class MonitoringFlags
{
    public const string Warning = "warning";
    public const string Critical = "critical";

    public static IReadOnlyList<MonitoringFlag> Evaluate(MonitoringFlagInput input, MonitoringThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(thresholds);
        var flags = new List<MonitoringFlag>();

        if (input.HealthStatus is "Degraded" or "Unhealthy")
        {
            flags.Add(new("health", input.HealthStatus == "Unhealthy" ? Critical : Warning,
                input.HealthStatus == "Unhealthy"
                    ? "La comprobación de salud falla: la base de datos o la cola no responden."
                    : "La comprobación de salud está degradada: la cola de webhooks va atrasada o falla."));
        }

        if (input.WebhookPending >= thresholds.WebhookBacklog)
        {
            flags.Add(new("webhook_backlog", Warning, $"Hay {input.WebhookPending} entregas de webhook pendientes (umbral {thresholds.WebhookBacklog})."));
        }

        if (input.OldestPending is { } pending && pending >= TimeSpan.FromMinutes(thresholds.WebhookOldestPendingMinutes))
        {
            flags.Add(new("webhook_backlog_age", Warning,
                $"La entrega pendiente más antigua lleva {Minutes(pending)} minutos esperando (umbral {thresholds.WebhookOldestPendingMinutes})."));
        }

        if (input.ClassificationWaiting >= thresholds.ClassificationBacklog)
        {
            flags.Add(new("classification_backlog", Warning,
                $"Hay {input.ClassificationWaiting} conversaciones esperando clasificación (umbral {thresholds.ClassificationBacklog})."));
        }

        if (input.OldestClassification is { } waiting && waiting >= TimeSpan.FromMinutes(thresholds.ClassificationOldestMinutes))
        {
            flags.Add(new("classification_backlog_age", Warning,
                $"La clasificación pendiente más antigua lleva {Minutes(waiting)} minutos esperando (umbral {thresholds.ClassificationOldestMinutes})."));
        }

        if (input.ClassificationStopped > 0)
        {
            flags.Add(new("classification_stopped", Critical,
                $"{input.ClassificationStopped} conversaciones dejaron de clasificarse tras fallar varias veces (se reintentan con el próximo mensaje)."));
        }

        if (input.Deliveries24h >= thresholds.MinDeliveriesForRate
            && input.FailedDeliveries24h * 100 >= thresholds.FailedDeliveryRatePercent * input.Deliveries24h)
        {
            flags.Add(new("failed_deliveries", Critical,
                $"Fallaron {input.FailedDeliveries24h} de {input.Deliveries24h} entregas de webhook en las últimas 24 horas (umbral {thresholds.FailedDeliveryRatePercent} %)."));
        }

        if (input.Errors24h >= thresholds.ErrorsLast24h)
        {
            flags.Add(new("errors", Warning, $"Se registraron {input.Errors24h} errores en las últimas 24 horas (umbral {thresholds.ErrorsLast24h})."));
        }

        if (input.Sends7d >= thresholds.MinSendsForRate && input.FailedSends7d * 100 >= thresholds.SendFailureRatePercent * input.Sends7d)
        {
            flags.Add(new("send_failures", Warning,
                $"No salieron {input.FailedSends7d} de {input.Sends7d} envíos manuales de los últimos 7 días (umbral {thresholds.SendFailureRatePercent} %)."));
        }

        if (input.SilentCompanies > 0)
        {
            flags.Add(new("silent_companies", Warning,
                $"{input.SilentCompanies} {(input.SilentCompanies == 1 ? "empresa" : "empresas")} con líneas activas no recibe(n) entregas reales hace al menos {thresholds.SilentLineHours} horas."));
        }

        if (input.AiLimitedCompanies > 0)
        {
            flags.Add(new("ai_limited_companies", Warning,
                $"{input.AiLimitedCompanies} {(input.AiLimitedCompanies == 1 ? "empresa agotó" : "empresas agotaron")} su cupo o su tope en USD de IA este mes: se clasifican solo con reglas."));
        }

        if (input.DeploymentCapReached)
        {
            flags.Add(new("ai_deployment_cap", Critical, "Se alcanzó el tope mensual en USD de IA de la plataforma: todas las empresas se clasifican solo con reglas hasta fin de mes."));
        }

        if (input.BillingEventsFailed > 0)
        {
            flags.Add(new("billing_events_failed", Critical,
                $"{input.BillingEventsFailed} eventos de la pasarela de pagos no se pudieron procesar (revisa el log de la API y docs/BILLING.md)."));
        }

        if (input.BillingNeedsReview > 0)
        {
            flags.Add(new("billing_payment_needs_review", Critical,
                $"{input.BillingNeedsReview} pagos necesitan revisión (un cobro doble, un pago sobre un cobro cerrado o un resultado desconocido): revísalos en la facturación de cada empresa."));
        }

        if (input.BillingAttemptsStuck > 0)
        {
            flags.Add(new("billing_attempt_stuck", Warning,
                $"{input.BillingAttemptsStuck} pagos llevan más de un día esperando respuesta de la pasarela."));
        }

        if (input.StuckDeletions > 0)
        {
            flags.Add(new("tenant_deletion_stuck", Critical,
                $"{input.StuckDeletions} eliminaciones de datos de empresas canceladas no terminan (se reintentan solas; revisa el log de la API)."));
        }

        if (input.LostUsageRecords > 0)
        {
            flags.Add(new("ai_usage_lost", Critical,
                $"{input.LostUsageRecords} registros de uso de IA no se pudieron guardar en esta instancia: su costo solo está en el log (evento AiUsageNotRecorded)."));
        }

        if (input.BreakerState is "Open" or "HalfOpen")
        {
            flags.Add(new("llm_breaker_open", Warning, "El proveedor de IA falla y el cortacircuitos está abierto: las conversaciones se leen con reglas mientras tanto."));
        }

        return flags;
    }

    private static long Minutes(TimeSpan span) => (long)span.TotalMinutes;
}
