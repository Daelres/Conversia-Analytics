using System.Text.Encodings.Web;
using System.Text.Json;
using Conversia.Domain.Common;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// One change made by a platform administrator, or by the system (a background job: billing, deletion): who, what (an action code), to which
/// company, why (a stable <see cref="ReasonCode"/>, apart from the free-text reason in the details) and the old and new values. Details hold
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
        public const string TenantSandboxModeChanged = "tenant.sandbox_mode_changed";
        public const string TenantAiEnabledChanged = "tenant.ai_enabled_changed";
        public const string TenantAiQuotaChanged = "tenant.ai_quota_changed";
        public const string TenantSendRateLimitChanged = "tenant.send_rate_limit_changed";
        public const string LicenseCreated = "license.created";
        public const string LicenseAssigned = "license.assigned";
        public const string LicenseReleased = "license.released";
        public const string LicenseRetired = "license.retired";
        public const string TenantDataDeletionStarted = "tenant.data_deletion_started";
        public const string TenantDataDeleted = "tenant.data_deleted";
    }

    /// <summary>The administrator who made the change; null when the system made it (see <see cref="IsSystem"/>).</summary>
    public Guid? AdminId { get; private set; }

    /// <summary>The stable code of why (<see cref="Common.ReasonCodes"/>): always there for a system change, optional for an administrator's.</summary>
    public string? ReasonCode { get; private set; }

    public bool IsSystem => AdminId is null;

    public string Action { get; private set; } = string.Empty;

    /// <summary>The company the change was made to. A column, not a foreign key: the trail outlives anything done to the company.</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>A small JSON object: the old and new values.</summary>
    public string Details { get; private set; } = "{}";

    // Required by EF Core for materialization.
    private PlatformAuditLog()
    {
    }

    public static PlatformAuditLog Record(
        Guid adminId, string action, Guid? tenantId, IReadOnlyDictionary<string, object?> details, string? reasonCode = null)
        => Create(TextRules.RequiredId(adminId, "El administrador es obligatorio."), action, tenantId, details, ReasonCodes.Optional(reasonCode));

    /// <summary>A change made by a background job: no administrator, and the reason code is required.</summary>
    public static PlatformAuditLog RecordSystem(string action, Guid? tenantId, string reasonCode, IReadOnlyDictionary<string, object?> details)
        => Create(null, action, tenantId, details, ReasonCodes.Required(reasonCode));

    private static PlatformAuditLog Create(Guid? adminId, string action, Guid? tenantId, IReadOnlyDictionary<string, object?> details, string? reasonCode)
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
            AdminId = adminId,
            Action = action,
            TenantId = tenantId,
            Details = json,
            ReasonCode = reasonCode,
        };
    }
}
