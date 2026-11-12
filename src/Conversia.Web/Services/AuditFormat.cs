using System.Text.Json;
using Conversia.Application.DTOs.Admin;
using Conversia.Domain.Entities;

namespace Conversia.Web.Services;

/// <summary>An audit entry in words: what changed, and the extra line (the reason the administrator typed, the number of the line) when there is one.</summary>
public sealed record AuditText(string Title, string? Detail);

/// <summary>Writes the audit trail the way a person reads it. An action it does not know is shown by its code, never hidden.</summary>
public static class AuditFormat
{
    public static AuditText Describe(AuditEntryResponse entry)
    {
        var details = entry.Details;
        return entry.Action switch
        {
            PlatformAuditLog.Actions.TenantStatusChanged => new AuditText(
                $"Cambió el estado: {TenantStatusLabels.Label(Text(details, "from"))} → {TenantStatusLabels.Label(Text(details, "to"))}"
                    + (Text(details, "cause") == "Billing" ? " (pago pendiente)" : string.Empty),
                Join(Text(details, "reason") is { Length: > 0 } reason ? $"Motivo: {reason}" : null, Cancellation(details))),
            PlatformAuditLog.Actions.TenantDataDeletionStarted => new AuditText(
                Text(details, "trigger") == "Manual" ? "Pidió eliminar los datos de la empresa ahora" : "Empezó la eliminación de los datos (venció el periodo de retención)",
                Reason(details)),
            PlatformAuditLog.Actions.TenantDataDeleted => new AuditText(
                "Eliminó todos los datos de la empresa", Rows(details) is { } rows ? $"{MetricsFormat.Whole(rows)} filas eliminadas" : null),
            PlatformAuditLog.Actions.TenantQuotaChanged => new AuditText(
                $"Cambió el cupo de líneas: {Text(details, "from") ?? MetricsFormat.NoData} → {Text(details, "to") ?? MetricsFormat.NoData}", null),
            PlatformAuditLog.Actions.ChannelDeactivated => new AuditText(
                "Desactivó una línea", Text(details, "displayPhoneNumber") is { Length: > 0 } number ? $"Número: {number}" : null),
            PlatformAuditLog.Actions.TenantSandboxModeChanged => new AuditText(
                Flag(details, "to") == true ? "Activó el modo sandbox" : "Desactivó el modo sandbox",
                Text(details, "reason") is { Length: > 0 } sandboxReason ? $"Motivo: {sandboxReason}" : null),
            PlatformAuditLog.Actions.TenantAiEnabledChanged => new AuditText(
                Flag(details, "from") == true && Flag(details, "to") == true ? "Actualizó los términos de IA aceptados"
                    : Flag(details, "to") == true ? "Activó la IA" : "Desactivó la IA",
                Join(Text(details, "reason") is { Length: > 0 } aiReason ? $"Motivo: {aiReason}" : null,
                    Text(details, "termsVersion") is { Length: > 0 } terms ? $"Términos: {terms}" : null)),
            PlatformAuditLog.Actions.TenantAiQuotaChanged => new AuditText(
                $"Cambió el cupo mensual de IA: {Text(details, "from") ?? DefaultWord} → {Text(details, "to") ?? DefaultWord}", Reason(details)),
            PlatformAuditLog.Actions.TenantSendRateLimitChanged => new AuditText(
                $"Cambió el límite de envíos por minuto: {Text(details, "from") ?? DefaultWord} → {Text(details, "to") ?? DefaultWord}", Reason(details)),
            PlatformAuditLog.Actions.LicenseCreated => new AuditText("Creó una licencia de línea", Number(details)),
            PlatformAuditLog.Actions.LicenseAssigned => new AuditText("Asignó una licencia de línea", Number(details)),
            PlatformAuditLog.Actions.LicenseReleased => new AuditText(
                Flag(details, "lineDeactivated") == true ? "Liberó una licencia de línea y desactivó su línea" : "Liberó una licencia de línea", Number(details)),
            PlatformAuditLog.Actions.LicenseRetired => new AuditText("Retiró una licencia de línea", Number(details)),
            _ => new AuditText($"Cambio registrado ({entry.Action})", null),
        };
    }

    private const string DefaultWord = "por defecto";

    // What a cancellation did: the lines switched off, the licences released.
    private static string? Cancellation(JsonElement details)
        => Text(details, "linesDeactivated") is { } lines && Text(details, "licensesReleased") is { } licenses
            ? $"Líneas desactivadas: {lines} · Licencias liberadas: {licenses}"
            : null;

    private static long? Rows(JsonElement details)
        => details.ValueKind == JsonValueKind.Object && details.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Object
            ? rows.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number).Sum(p => p.Value.GetInt64())
            : null;

    /// <summary>Who made the change and, with a code, why: the administrator's email, or the system.</summary>
    public static string Actor(AuditEntryResponse entry)
        => (entry.AdminId is null ? "el sistema" : entry.AdminEmail ?? "un administrador")
           + (entry.ReasonCode is { Length: > 0 } code ? $" ({ReasonCodeLabel(code)})" : string.Empty);

    /// <summary>The reason codes of the system in words; an unknown one is shown by its code.</summary>
    public static string ReasonCodeLabel(string code) => code switch
    {
        Conversia.Domain.Common.ReasonCodes.BillingPastDue => "pago vencido",
        Conversia.Domain.Common.ReasonCodes.BillingPaid => "pago recibido",
        Conversia.Domain.Common.ReasonCodes.BillingNonPayment => "falta de pago",
        Conversia.Domain.Common.ReasonCodes.DeletionRetentionExpired => "venció el periodo de retención",
        Conversia.Domain.Common.ReasonCodes.DeletionRequestedByAdmin => "pedido por un administrador",
        Conversia.Domain.Common.ReasonCodes.DeletionCompleted => "eliminación terminada",
        Conversia.Domain.Common.AdminReasonCodes.ContractSigned => "acuerdo comercial firmado",
        Conversia.Domain.Common.AdminReasonCodes.AgreedPause => "pausa acordada con la empresa",
        Conversia.Domain.Common.AdminReasonCodes.CustomerRequest => "solicitud de la empresa",
        Conversia.Domain.Common.AdminReasonCodes.ContractEnded => "fin del contrato",
        Conversia.Domain.Common.AdminReasonCodes.PaymentIssue => "problema con el pago",
        Conversia.Domain.Common.AdminReasonCodes.Abuse => "uso indebido",
        Conversia.Domain.Common.AdminReasonCodes.Other => "otro",
        _ => code,
    };

    private static string? Reason(JsonElement details) => Text(details, "reason") is { Length: > 0 } reason ? $"Motivo: {reason}" : null;

    // The masked number the audit keeps for a licence ("•••• 1234").
    private static string? Number(JsonElement details) => Text(details, "number") is { Length: > 0 } number ? $"Número: {number}" : null;

    private static string? Join(string? first, string? second)
        => first is null ? second : second is null ? first : $"{first} · {second}";

    private static bool? Flag(JsonElement details, string name)
        => details.ValueKind == JsonValueKind.Object && details.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    // Numbers and strings alike come back as text; anything else (or a body that is not an object) is "absent".
    private static string? Text(JsonElement details, string name)
    {
        if (details.ValueKind != JsonValueKind.Object || !details.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}
