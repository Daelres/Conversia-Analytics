using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;
using System.Text.Json.Serialization;
using Conversia.Domain.Enums;
using Conversia.Domain.ReadModels;

namespace Conversia.Application.DTOs.Admin;

/// <summary>The answer to a platform administrator's login. The token is only good for the admin API.</summary>
public class AdminAuthResponse
{
    public string Token { get; set; } = string.Empty;

    public Guid AdminId { get; set; }

    public DateTime ExpiresAt { get; set; }
}

/// <summary>Query string of <c>GET /api/admin/tenants</c>.</summary>
public class AdminTenantsQuery
{
    public const int MaxPageSize = 100;

    [Display(Name = "Página")]
    [Range(1, int.MaxValue, ErrorMessage = ValidationMessages.Range)]
    public int Page { get; set; } = 1;

    [Display(Name = "Tamaño de página")]
    [Range(1, MaxPageSize, ErrorMessage = ValidationMessages.Range)]
    public int PageSize { get; set; } = 20;

    [Display(Name = "Estado")]
    [EnumName(typeof(TenantStatus))]
    public string? Status { get; set; }

    /// <summary>Part of the company name, ignoring case.</summary>
    [Display(Name = "Búsqueda")]
    [TrimmedText(100, Required = false)]
    public string? Search { get; set; }
}

/// <summary>Query string of <c>GET /api/admin/audit</c>.</summary>
public class AdminAuditQuery
{
    [Display(Name = "Página")]
    [Range(1, int.MaxValue, ErrorMessage = ValidationMessages.Range)]
    public int Page { get; set; } = 1;

    [Display(Name = "Tamaño de página")]
    [Range(1, AdminTenantsQuery.MaxPageSize, ErrorMessage = ValidationMessages.Range)]
    public int PageSize { get; set; } = 20;

    [Display(Name = "Empresa")]
    public Guid? TenantId { get; set; }
}

public class ChangeTenantStatusRequest
{
    [Display(Name = "Estado")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [EnumName(typeof(TenantStatus))]
    public string Status { get; set; } = string.Empty;

    [Display(Name = "Motivo")]
    [TrimmedText(Tenant.StatusReasonMaxLength, Required = false)]
    public string? Reason { get; set; }

    /// <summary>
    /// One of <see cref="Domain.Common.AdminReasonCodes.All"/> (optional): the reason from the closed list, kept as a code. The free text above
    /// is then only the detail; it is kept forever in the audit trail, so it must never hold personal data.
    /// </summary>
    [Display(Name = "Motivo de la lista")]
    [TrimmedText(64, Required = false)]
    public string? ReasonCode { get; set; }
}

/// <summary>
/// Body of <c>POST /api/admin/tenants/{id}/data-deletion</c>: delete a cancelled company's data now, before its retention window ends. The
/// administrator types the company's name (the strong confirmation) and the reason (audited).
/// </summary>
public class DeleteTenantDataRequest
{
    public const int ReasonMaxLength = 200;

    [Display(Name = "Nombre de la empresa")]
    [TrimmedText(200)]
    public string? ConfirmName { get; set; }

    [Display(Name = "Motivo")]
    [TrimmedText(ReasonMaxLength)]
    public string? Reason { get; set; }
}

public class SetLineQuotaRequest
{
    [Display(Name = "Cupo de líneas activas")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [Range(0, Tenant.MaxLinesLimit, ErrorMessage = ValidationMessages.Range)]
    public int? MaxActiveLines { get; set; }
}

/// <summary>Body of <c>PUT /api/admin/tenants/{id}/sandbox-mode</c>: switch the internal sandbox of a company on or off, and why.</summary>
public class SetSandboxModeRequest
{
    [Display(Name = "Modo sandbox")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public bool? Enabled { get; set; }

    [Display(Name = "Motivo")]
    [TrimmedText(Tenant.SandboxModeReasonMaxLength)]
    public string? Reason { get; set; }
}

/// <summary>A company in the administrator's listing.</summary>
public class TenantSummaryResponse
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TenantStatus Status { get; set; }

    public int MaxActiveLines { get; set; }

    public int ActiveLines { get; set; }

    public int RegisteredLines { get; set; }

    public string? OwnerEmail { get; set; }

    public DateTime RegisteredAt { get; set; }

    /// <summary>The time of the company's latest message; null when it has none.</summary>
    public DateTime? LastActivityAt { get; set; }

    public DateTime? StatusChangedAt { get; set; }

    public string? StatusReason { get; set; }

    /// <summary>Whether the company is in the internal sandbox mode (docs/SANDBOX.md).</summary>
    public bool SandboxMode { get; set; }

    /// <summary>Whether the LLM may read the company's conversations (docs/CLASSIFICATION.md, "Usage and quota").</summary>
    public bool AiEnabled { get; set; }

    /// <summary>Why it is suspended (<c>Manual</c> or <c>Billing</c>); null in every other status.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SuspensionCause? SuspensionCause { get; set; }

    /// <summary>The stable code of the last change the system made (for example <c>billing.past_due</c>).</summary>
    public string? StatusReasonCode { get; set; }

    public DateTime? CancelledAt { get; set; }

    /// <summary>When the deletion of a cancelled company's data is due.</summary>
    public DateTime? DataDeletionDueAt { get; set; }

    /// <summary>When it started; from then on the company cannot be reactivated.</summary>
    public DateTime? DataDeletionStartedAt { get; set; }

    public static TenantSummaryResponse From(TenantAdminSummary s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Status = s.Status,
        MaxActiveLines = s.MaxActiveLines,
        ActiveLines = s.ActiveLines,
        RegisteredLines = s.RegisteredLines,
        OwnerEmail = s.OwnerEmail,
        RegisteredAt = s.RegisteredAt,
        LastActivityAt = s.LastActivityAt,
        StatusChangedAt = s.StatusChangedAt,
        StatusReason = s.StatusReason,
        SandboxMode = s.SandboxMode,
        AiEnabled = s.AiEnabled,
        SuspensionCause = s.SuspensionCause,
        StatusReasonCode = s.StatusReasonCode,
        CancelledAt = s.CancelledAt,
        DataDeletionDueAt = s.DataDeletionDueAt,
        DataDeletionStartedAt = s.DataDeletionStartedAt,
    };
}

/// <summary>A WhatsApp line of a company: business facts only, no customer data and no webhook address.</summary>
public class TenantLineResponse
{
    public Guid ChannelId { get; set; }

    public string DisplayPhoneNumber { get; set; } = string.Empty;

    public string? BranchName { get; set; }

    public bool IsActive { get; set; }

    public static TenantLineResponse From(TenantAdminLine l) => new()
    {
        ChannelId = l.ChannelId,
        DisplayPhoneNumber = l.DisplayPhoneNumber,
        BranchName = l.BranchName,
        IsActive = l.IsActive,
    };
}

public class TenantDetailResponse : TenantSummaryResponse
{
    public IReadOnlyList<TenantLineResponse> Lines { get; set; } = [];

    public IReadOnlyList<AuditEntryResponse> RecentAudit { get; set; } = [];

    /// <summary>The company's limits as they apply now (line quota, AI switch and quota, send rate limit).</summary>
    public TenantLimitsResponse Limits { get; set; } = new();

    /// <summary>The line licences assigned to the company, numbers masked.</summary>
    public IReadOnlyList<LineLicenseResponse> Licenses { get; set; } = [];

    /// <summary>The days a cancelled company's data is kept before it is deleted (<c>Tenants:Deletion:RetentionDays</c>).</summary>
    public int DeletionRetentionDays { get; set; }
}

public class AuditEntryResponse
{
    public Guid Id { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>The administrator who made the change; null when the system did (a background job: billing, deletion).</summary>
    public Guid? AdminId { get; set; }

    public string? AdminEmail { get; set; }

    /// <summary>The stable code of why (always there for a system change).</summary>
    public string? ReasonCode { get; set; }

    public string Action { get; set; } = string.Empty;

    public Guid? TenantId { get; set; }

    /// <summary>The old and new values of the change, as a small JSON object.</summary>
    public JsonElement Details { get; set; }

    public static AuditEntryResponse From(PlatformAuditEntry e) => new()
    {
        Id = e.Id,
        OccurredAt = e.OccurredAt,
        AdminId = e.AdminId,
        AdminEmail = e.AdminEmail,
        ReasonCode = e.ReasonCode,
        Action = e.Action,
        TenantId = e.TenantId,
        Details = ParseDetails(e.Details),
    };

    private static JsonElement ParseDetails(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }
}
