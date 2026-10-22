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
                $"Cambió el estado: {TenantStatusLabels.Label(Text(details, "from"))} → {TenantStatusLabels.Label(Text(details, "to"))}",
                Text(details, "reason") is { Length: > 0 } reason ? $"Motivo: {reason}" : null),
            PlatformAuditLog.Actions.TenantQuotaChanged => new AuditText(
                $"Cambió el cupo de líneas: {Text(details, "from") ?? MetricsFormat.NoData} → {Text(details, "to") ?? MetricsFormat.NoData}", null),
            PlatformAuditLog.Actions.ChannelDeactivated => new AuditText(
                "Desactivó una línea", Text(details, "displayPhoneNumber") is { Length: > 0 } number ? $"Número: {number}" : null),
            _ => new AuditText($"Cambio registrado ({entry.Action})", null),
        };
    }

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
