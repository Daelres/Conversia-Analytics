using System.Text.Encodings.Web;
using System.Text.Json;
using Conversia.Domain.Common;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// One change made by a platform administrator: who, what (an action code), to which company and the old and new values. Details hold
/// ids, statuses and numbers only (never a secret, a message or customer data). Platform-level, so no tenant query filter applies.
/// </summary>
public class PlatformAuditLog : BaseEntity
{
    public const int ActionMaxLength = 64;
    public const int DetailsMaxLength = 4000;

    // Accents stay as typed (the default encoder would turn each into a six-character escape and inflate the size).
    private static readonly JsonSerializerOptions DetailsJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static class Actions
    {
        public const string TenantStatusChanged = "tenant.status_changed";
        public const string TenantQuotaChanged = "tenant.quota_changed";
        public const string ChannelDeactivated = "channel.deactivated";
    }

    public Guid AdminId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    /// <summary>The company the change was made to. A column, not a foreign key: the trail outlives anything done to the company.</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>A small JSON object: the old and new values.</summary>
    public string Details { get; private set; } = "{}";

    // Required by EF Core for materialization.
    private PlatformAuditLog()
    {
    }

    public static PlatformAuditLog Record(Guid adminId, string action, Guid? tenantId, IReadOnlyDictionary<string, object?> details)
    {
        if (string.IsNullOrWhiteSpace(action) || action.Length > ActionMaxLength)
        {
            throw new DomainException("La acción de auditoría no es válida.");
        }

        var json = JsonSerializer.Serialize(details, DetailsJson);
        if (json.Length > DetailsMaxLength)
        {
            throw new DomainException("El detalle de auditoría es demasiado grande.");
        }

        return new PlatformAuditLog
        {
            AdminId = TextRules.RequiredId(adminId, "El administrador es obligatorio."),
            Action = action,
            TenantId = tenantId,
            Details = json,
        };
    }
}
