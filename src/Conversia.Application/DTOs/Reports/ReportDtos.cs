using System.ComponentModel.DataAnnotations;
using Conversia.Application.Reports;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Reports;

/// <summary>One person of a report definition and how they receive it.</summary>
public class ReportRecipientRequest
{
    [Required(ErrorMessage = "Indica la persona que recibe el informe.")]
    public Guid? UserId { get; set; }

    public bool Email { get; set; }

    public bool WhatsApp { get; set; }
}

/// <summary><c>POST</c>/<c>PUT /api/reports/definitions</c>: the whole configuration of a report (docs/REPORTS.md, "Definitions").</summary>
public class ReportDefinitionRequest
{
    [Display(Name = "Nombre")]
    [Required(ErrorMessage = "El nombre del informe es obligatorio.")]
    [NoNullCharacters]
    public string? Name { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary><c>Daily</c>, <c>Weekly</c> or <c>Monthly</c>.</summary>
    [Display(Name = "Frecuencia")]
    [Required(ErrorMessage = "La frecuencia es obligatoria.")]
    [EnumName(typeof(ReportFrequency))]
    public string? Frequency { get; set; }

    /// <summary>1 = Monday ... 7 = Sunday (weekly reports).</summary>
    [Range(1, 7, ErrorMessage = ReportDefinition.DayRuleMessage)]
    public int SendDayOfWeek { get; set; } = ReportDefinition.DefaultSendDayOfWeek;

    [Range(0, 23, ErrorMessage = ReportDefinition.HourRuleMessage)]
    public int SendHour { get; set; } = ReportDefinition.DefaultSendHour;

    /// <summary>Empty = every branch.</summary>
    public List<Guid> BranchIds { get; set; } = [];

    /// <summary>Names of <see cref="ReportSections"/> (<c>Headline</c>, <c>Conversion</c>, ...).</summary>
    public List<string> Sections { get; set; } = [];

    public List<ReportRecipientRequest> Recipients { get; set; } = [];

    public Guid? WhatsAppChannelId { get; set; }

    public string? WhatsAppTemplateName { get; set; }

    public string? WhatsAppTemplateLanguage { get; set; }
}

public sealed record ReportRecipientResponse(
    Guid UserId, string Email, bool ByEmail, bool ByWhatsApp, DateTime? EmailUnsubscribedAt, bool WhatsAppOptedIn);

public sealed record ReportDefinitionResponse(
    Guid Id,
    string Name,
    bool IsActive,
    string Frequency,
    int SendDayOfWeek,
    int SendHour,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<string> Sections,
    IReadOnlyList<ReportRecipientResponse> Recipients,
    Guid? WhatsAppChannelId,
    string? WhatsAppTemplateName,
    string? WhatsAppTemplateLanguage,
    DateTime NextSendAt,
    ReportPeriodResponse NextPeriod,
    DateTime CreatedAt);

/// <param name="To">Exclusive.</param>
public sealed record ReportPeriodResponse(DateOnly From, DateOnly To, string Label);

public sealed record ReportUserOption(Guid Id, string Email, bool WhatsAppOptedIn);

public sealed record ReportBranchOption(Guid Id, string Name);

/// <summary>An approved utility template of one of the company's lines with exactly three placeholders: period, headline, link.</summary>
public sealed record ReportTemplateOption(Guid ChannelId, string ChannelPhoneNumber, string Name, string Language, string? BodyText);

public sealed record ReportSectionOption(string Code, string Label);

/// <summary><c>GET /api/reports/options</c>: what a definition can be made of, and the limits.</summary>
public sealed record ReportOptionsResponse(
    IReadOnlyList<ReportUserOption> Users,
    IReadOnlyList<ReportBranchOption> Branches,
    IReadOnlyList<ReportTemplateOption> Templates,
    IReadOnlyList<ReportSectionOption> Sections,
    int Definitions,
    int MaxDefinitions,
    int MaxRecipients);

public sealed record ReportDeliveryCounts(int Sent, int Failed, int Pending, int Skipped);

public sealed record ReportSummaryResponse(
    Guid Id,
    Guid DefinitionId,
    string DefinitionName,
    string Frequency,
    ReportPeriodResponse Period,
    string Generator,
    string Headline,
    bool IsSandbox,
    DateTime GeneratedAt,
    ReportDeliveryCounts Deliveries);

/// <param name="ErrorCode">A stable code; the Web words it.</param>
public sealed record ReportDeliveryResponse(string Email, string Channel, string Status, int Attempts, string? ErrorCode, DateTime? SentAt);

/// <summary><c>GET /api/reports/{id}</c>: a generated report as a manager reads it, and how it was delivered.</summary>
public sealed record ReportResponse(
    Guid Id,
    Guid DefinitionId,
    string DefinitionName,
    string Company,
    string Frequency,
    ReportPeriodResponse Period,
    string Generator,
    bool IsSandbox,
    DateTime GeneratedAt,
    ReportContent Content,
    IReadOnlyList<ReportDeliveryResponse> Deliveries);

/// <summary><c>POST /api/reports/definitions/{id}/preview</c>: the report the definition would send now, written but not stored or sent.</summary>
public sealed record ReportPreviewResponse(ReportPeriodResponse Period, string Generator, string? FallbackReason, ReportContent Content);

/// <summary>The caller's own WhatsApp opt-in for the reports (docs/REPORTS.md, "WhatsApp opt-in").</summary>
/// <param name="OptedIn">The consent is recorded (the number may still be waiting for its code).</param>
/// <param name="Verified">The code sent to the number came back: only a verified number receives reports.</param>
/// <param name="CodeSent">After an opt-in: whether a code could be sent (false when the company has no approved report template or line yet).</param>
public sealed record ReportWhatsAppOptInResponse(
    bool OptedIn, string? PhoneNumber, DateTime? OptedInAt, DateTime? OptedOutAt, bool Verified = false, DateTime? VerifiedAt = null, bool? CodeSent = null);

public class ReportWhatsAppVerifyRequest
{
    [Required(ErrorMessage = "Escribe el código que te enviamos por WhatsApp.")]
    [StringLength(6, MinimumLength = 6, ErrorMessage = "El código tiene 6 dígitos.")]
    public string? Code { get; set; }
}

public class ReportWhatsAppOptInRequest
{
    [Display(Name = "Número de WhatsApp")]
    [Required(ErrorMessage = "El número de WhatsApp es obligatorio.")]
    [NoNullCharacters]
    public string? PhoneNumber { get; set; }

    /// <summary>The explicit consent: must be true.</summary>
    public bool Consent { get; set; }
}

public class ReportUnsubscribeRequest
{
    [Required(ErrorMessage = "El enlace no es válido.")]
    [StringLength(100, ErrorMessage = "El enlace no es válido.")]
    public string? Token { get; set; }
}

public sealed record ReportUnsubscribeResponse(string DefinitionName);
