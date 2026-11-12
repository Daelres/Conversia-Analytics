using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.ReadModels;

namespace Conversia.Application.DTOs.Admin;

/// <summary>Body of <c>PUT /api/admin/tenants/{id}/ai-enabled</c>: the AI switch, why, and the version of the terms the company accepted.</summary>
public class SetAiEnabledRequest
{
    [Display(Name = "IA")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public bool? Enabled { get; set; }

    [Display(Name = "Motivo")]
    [TrimmedText(Tenant.AiChangeReasonMaxLength)]
    public string? Reason { get; set; }

    /// <summary>Required to switch the AI on; ignored to switch it off.</summary>
    [Display(Name = "Versión de los términos")]
    [TrimmedText(Tenant.AiTermsVersionMaxLength, Required = false)]
    public string? TermsVersion { get; set; }
}

/// <summary>Body of <c>PUT /api/admin/tenants/{id}/ai-quota</c>. Null resets to the deployment default.</summary>
public class SetAiQuotaRequest
{
    [Display(Name = "Cupo mensual de conversaciones con IA")]
    [Range(0, Tenant.MaxAiMonthlyConversationQuota, ErrorMessage = ValidationMessages.Range)]
    public int? MonthlyConversations { get; set; }

    /// <summary>Why it changes: required and audited, like the AI switch's reason.</summary>
    [Display(Name = "Motivo")]
    [TrimmedText(Tenant.AiChangeReasonMaxLength)]
    public string? Reason { get; set; }
}

/// <summary>Body of <c>PUT /api/admin/tenants/{id}/send-rate-limit</c>. Null resets to the deployment default.</summary>
public class SetSendRateLimitRequest
{
    [Display(Name = "Límite de envíos por minuto")]
    [Range(1, Tenant.MaxSendRateLimitPerMinute, ErrorMessage = ValidationMessages.Range)]
    public int? PerMinute { get; set; }

    /// <summary>Why it changes: required and audited.</summary>
    [Display(Name = "Motivo")]
    [TrimmedText(Tenant.AiChangeReasonMaxLength)]
    public string? Reason { get; set; }
}

/// <summary>The limits of a company as they apply now: each effective value and whether it is the deployment default.</summary>
public class TenantLimitsResponse
{
    public int MaxActiveLines { get; set; }

    public int ActiveLines { get; set; }

    public bool AiEnabled { get; set; }

    /// <summary>The version of the AI terms the company accepted when the AI was last switched on.</summary>
    public string? AiTermsVersion { get; set; }

    public DateTime? AiEnabledChangedAt { get; set; }

    /// <summary>The effective monthly quota of distinct conversations analysed with AI.</summary>
    public int AiMonthlyConversationQuota { get; set; }

    public bool AiQuotaIsDefault { get; set; }

    public int DefaultAiMonthlyConversationQuota { get; set; }

    /// <summary>The company's monthly USD ceiling (the quota times <c>Classification:Quota:TenantMonthlyUsdPerQuotaUnit</c>).</summary>
    public decimal AiMonthlyUsdCeiling { get; set; }

    public int SendRateLimitPerMinute { get; set; }

    public bool SendRateLimitIsDefault { get; set; }

    public int DefaultSendRateLimitPerMinute { get; set; }

    /// <summary>The most a company's own send limit may be (<c>Messaging:TenantSendRateLimitMaxPerMinute</c>).</summary>
    public int MaxSendRateLimitPerMinute { get; set; }
}

/// <summary>Query string of <c>GET /api/admin/licenses</c>.</summary>
public class AdminLicensesQuery
{
    [Display(Name = "Página")]
    [Range(1, int.MaxValue, ErrorMessage = ValidationMessages.Range)]
    public int Page { get; set; } = 1;

    [Display(Name = "Tamaño de página")]
    [Range(1, AdminTenantsQuery.MaxPageSize, ErrorMessage = ValidationMessages.Range)]
    public int PageSize { get; set; } = 20;

    [Display(Name = "Estado")]
    [EnumName(typeof(LineLicenseStatus))]
    public string? Status { get; set; }

    [Display(Name = "Empresa")]
    public Guid? TenantId { get; set; }
}

/// <summary>Body of <c>POST /api/admin/licenses</c>.</summary>
public class CreateLineLicenseRequest
{
    [Display(Name = "Número")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [TrimmedText(WhatsAppChannel.DisplayPhoneNumberMaxLength)]
    public string PhoneNumber { get; set; } = string.Empty;

    [Display(Name = "Identificador en 360dialog")]
    [TrimmedText(WhatsAppChannel.ProviderPhoneNumberIdMaxLength, Required = false)]
    public string? ProviderChannelId { get; set; }

    [Display(Name = "Costo mensual (USD)")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [Range(typeof(decimal), "0", "10000", ErrorMessage = ValidationMessages.Range, ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? MonthlyCostUsd { get; set; }

    /// <summary>The number belongs to the customer (cost 0 to the platform).</summary>
    public bool CustomerOwned { get; set; }

    [Display(Name = "Notas")]
    [TrimmedText(LineLicense.NotesMaxLength, Required = false)]
    public string? Notes { get; set; }
}

/// <summary>Body of <c>POST /api/admin/licenses/{id}/assign</c>.</summary>
public class AssignLineLicenseRequest
{
    [Display(Name = "Empresa")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public Guid? TenantId { get; set; }
}

/// <summary>A licence as the administrator sees it: the number masked to its last four digits.</summary>
public class LineLicenseResponse
{
    public Guid Id { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string PhoneNumberMasked { get; set; } = string.Empty;

    public string? ProviderChannelId { get; set; }

    public decimal MonthlyCostUsd { get; set; }

    public bool CustomerOwned { get; set; }

    /// <summary><c>Available</c>, <c>Assigned</c> or <c>Retired</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public Guid? AssignedTenantId { get; set; }

    public string? AssignedTenantName { get; set; }

    /// <summary>The company line the licence is linked to, when the company registered or switched the number on.</summary>
    public Guid? ChannelId { get; set; }

    public DateTime? AssignedAt { get; set; }

    public DateTime? ReleasedAt { get; set; }

    public DateTime? RetiredAt { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }

    public static LineLicenseResponse From(LineLicenseRow r) => new()
    {
        Id = r.Id,
        Provider = r.Provider,
        PhoneNumberMasked = r.PhoneNumberMasked,
        ProviderChannelId = r.ProviderChannelId,
        MonthlyCostUsd = r.MonthlyCostUsd,
        CustomerOwned = r.CustomerOwned,
        Status = r.Status.ToString(),
        AssignedTenantId = r.AssignedTenantId,
        AssignedTenantName = r.AssignedTenantName,
        ChannelId = r.ChannelId,
        AssignedAt = r.AssignedAt,
        ReleasedAt = r.ReleasedAt,
        RetiredAt = r.RetiredAt,
        Notes = r.Notes,
        CreatedAt = r.CreatedAt,
    };

    public static LineLicenseResponse From(LineLicense l, string? tenantName = null) => From(new LineLicenseRow(
        l.Id, l.Provider, l.MaskedPhoneNumber, l.ProviderChannelId, l.MonthlyCostUsd, l.CustomerOwned, l.Status, l.AssignedTenantId, tenantName, l.ChannelId,
        l.AssignedAt, l.ReleasedAt, l.RetiredAt, l.Notes, l.CreatedAt));
}
