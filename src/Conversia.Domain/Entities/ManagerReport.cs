using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>One recipient of a report definition as the manager configures it: a user of the company and the ways they receive it.</summary>
public sealed record ReportRecipientSetting(Guid UserId, ReportChannels Channels);

/// <summary>
/// Everything a manager sets on a report definition at once, so the rules that tie fields together (a WhatsApp recipient needs a template)
/// are checked on the whole (docs/REPORTS.md, "Definitions").
/// </summary>
/// <param name="SendDayOfWeek">1 = Monday ... 7 = Sunday; used by weekly reports only.</param>
/// <param name="SendHour">The local hour (America/Bogota) the report is sent, 0 to 23.</param>
/// <param name="BranchIds">The branches the report covers; empty = all of them.</param>
public sealed record ReportDefinitionSettings(
    string Name,
    bool IsActive,
    ReportFrequency Frequency,
    int SendDayOfWeek,
    int SendHour,
    IReadOnlyCollection<Guid> BranchIds,
    ReportSections Sections,
    IReadOnlyCollection<ReportRecipientSetting> Recipients,
    Guid? WhatsAppChannelId = null,
    string? WhatsAppTemplateName = null,
    string? WhatsAppTemplateLanguage = null);

/// <summary>
/// An automatic summary a company sends its managers (docs/REPORTS.md): who receives it and how, how often, which branches and which parts
/// of the analysis. The generated reports and their deliveries hang from it.
/// </summary>
public class ReportDefinition : BaseEntity
{
    public const int NameMaxLength = 100;

    public const int MaxBranches = 100;

    /// <summary>The most recipients one definition can name (the deployment can lower it: <c>Reports:MaxRecipientsPerDefinition</c>).</summary>
    public const int MaxRecipients = 50;

    public const int DefaultSendHour = 7;

    public const int DefaultSendDayOfWeek = 1;

    public static readonly string NameRuleMessage = $"El nombre del informe {TextRules.RuleText(NameMaxLength)}.";
    public const string FrequencyRuleMessage = "La frecuencia debe ser diaria, semanal o mensual.";
    public const string HourRuleMessage = "La hora de envío debe estar entre 0 y 23.";
    public const string DayRuleMessage = "El día de envío debe estar entre 1 (lunes) y 7 (domingo).";
    public const string SectionsRuleMessage = "Elige al menos una parte del análisis para el informe.";
    public static readonly string BranchesRuleMessage = $"Elige como máximo {MaxBranches} sucursales.";
    public const string RecipientsRequiredMessage = "Elige al menos una persona que reciba el informe.";
    public const string RecipientChannelMessage = "Cada persona debe recibir el informe por correo, por WhatsApp o por ambos.";
    public const string RecipientDuplicateMessage = "Una persona aparece más de una vez entre quienes reciben el informe.";
    public const string WhatsAppTemplateRequiredMessage = "Para enviar el informe por WhatsApp elige el número y la plantilla aprobada.";
    public const string WhatsAppTemplateIncompleteMessage = "El número, la plantilla y su idioma se eligen juntos.";

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public ReportFrequency Frequency { get; private set; }

    public int SendDayOfWeek { get; private set; } = DefaultSendDayOfWeek;

    public int SendHour { get; private set; } = DefaultSendHour;

    /// <summary>Empty = every branch of the company, also the ones added later.</summary>
    public Guid[] BranchIds { get; private set; } = [];

    public ReportSections Sections { get; private set; }

    /// <summary>The company's line the WhatsApp notice leaves from. No foreign key: a line or template that disappears fails the delivery, not the definition.</summary>
    public Guid? WhatsAppChannelId { get; private set; }

    public string? WhatsAppTemplateName { get; private set; }

    public string? WhatsAppTemplateLanguage { get; private set; }

    private readonly List<ReportRecipient> _recipients = [];

    public IReadOnlyCollection<ReportRecipient> Recipients => _recipients;

    // Required by EF Core for materialization.
    private ReportDefinition()
    {
    }

    public static ReportDefinition Create(Guid tenantId, ReportDefinitionSettings settings, int maxRecipients = MaxRecipients)
    {
        var definition = new ReportDefinition { TenantId = TextRules.RequiredId(tenantId, "El identificador de la empresa es obligatorio.") };
        definition.Update(settings, maxRecipients);
        return definition;
    }

    /// <summary>
    /// Replaces the whole configuration. A recipient that stays keeps its row (and so the record of an unsubscribe from the email); one that
    /// is removed and added again starts over.
    /// </summary>
    public void Update(ReportDefinitionSettings settings, int maxRecipients = MaxRecipients)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var name = TextRules.Required(settings.Name, NameMaxLength, NameRuleMessage);
        if (!Enum.IsDefined(settings.Frequency))
        {
            throw new DomainException(FrequencyRuleMessage);
        }

        if (settings.SendHour is < 0 or > 23)
        {
            throw new DomainException(HourRuleMessage);
        }

        if (settings.SendDayOfWeek is < 1 or > 7)
        {
            throw new DomainException(DayRuleMessage);
        }

        var sections = settings.Sections & ReportSections.All;
        if (sections == ReportSections.None || sections != settings.Sections)
        {
            throw new DomainException(SectionsRuleMessage);
        }

        var branches = (settings.BranchIds ?? []).Where(b => b != Guid.Empty).Distinct().ToArray();
        if (branches.Length > MaxBranches)
        {
            throw new DomainException(BranchesRuleMessage);
        }

        var recipients = settings.Recipients ?? [];
        if (recipients.Count == 0)
        {
            throw new DomainException(RecipientsRequiredMessage);
        }

        var limit = Math.Clamp(maxRecipients, 1, MaxRecipients);
        if (recipients.Count > limit)
        {
            throw new DomainException($"Un informe puede tener como máximo {limit} personas que lo reciben.");
        }

        if (recipients.Any(r => r.UserId == Guid.Empty || (r.Channels & ReportChannels.Both) == ReportChannels.None || (r.Channels & ~ReportChannels.Both) != 0))
        {
            throw new DomainException(RecipientChannelMessage);
        }

        if (recipients.Select(r => r.UserId).Distinct().Count() != recipients.Count)
        {
            throw new DomainException(RecipientDuplicateMessage);
        }

        var templateName = string.IsNullOrWhiteSpace(settings.WhatsAppTemplateName) ? null : settings.WhatsAppTemplateName.Trim();
        var templateLanguage = string.IsNullOrWhiteSpace(settings.WhatsAppTemplateLanguage) ? null : settings.WhatsAppTemplateLanguage.Trim();
        var templateParts = new object?[] { settings.WhatsAppChannelId, templateName, templateLanguage }.Count(p => p is not null);
        if (templateParts is not 0 and not 3)
        {
            throw new DomainException(WhatsAppTemplateIncompleteMessage);
        }

        if (templateName is not null && (templateName.Length > MessageTemplate.NameMaxLength || templateLanguage!.Length > MessageTemplate.LanguageMaxLength))
        {
            throw new DomainException(WhatsAppTemplateIncompleteMessage);
        }

        if (templateParts == 0 && recipients.Any(r => r.Channels.HasFlag(ReportChannels.WhatsApp)))
        {
            throw new DomainException(WhatsAppTemplateRequiredMessage);
        }

        Name = name;
        IsActive = settings.IsActive;
        Frequency = settings.Frequency;
        SendDayOfWeek = settings.SendDayOfWeek;
        SendHour = settings.SendHour;
        BranchIds = branches;
        Sections = sections;
        WhatsAppChannelId = settings.WhatsAppChannelId;
        WhatsAppTemplateName = templateName;
        WhatsAppTemplateLanguage = templateLanguage;

        _recipients.RemoveAll(existing => recipients.All(r => r.UserId != existing.UserId));
        foreach (var wanted in recipients)
        {
            var existing = _recipients.FirstOrDefault(r => r.UserId == wanted.UserId);
            if (existing is null)
            {
                _recipients.Add(ReportRecipient.Create(TenantId, Id, wanted.UserId, wanted.Channels));
            }
            else
            {
                existing.ChangeChannels(wanted.Channels);
            }
        }
    }
}

/// <summary>One person who receives a report definition, and how. The email address is the user's own, read at send time.</summary>
public class ReportRecipient
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid TenantId { get; private set; }

    public Guid DefinitionId { get; private set; }

    public Guid UserId { get; private set; }

    public ReportChannels Channels { get; private set; }

    /// <summary>When the person used the unsubscribe link of the email: no more emails of this report (WhatsApp has its own opt-in).</summary>
    public DateTime? EmailUnsubscribedAt { get; private set; }

    private ReportRecipient()
    {
    }

    internal static ReportRecipient Create(Guid tenantId, Guid definitionId, Guid userId, ReportChannels channels)
        => new() { TenantId = tenantId, DefinitionId = definitionId, UserId = userId, Channels = channels };

    internal void ChangeChannels(ReportChannels channels) => Channels = channels;

    /// <summary>Idempotent: a second click keeps the first instant.</summary>
    public void UnsubscribeEmail(DateTime utcNow) => EmailUnsubscribedAt ??= UtcTime.Normalize(utcNow);

    public bool ReceivesEmail => Channels.HasFlag(ReportChannels.Email) && EmailUnsubscribedAt is null;

    public bool ReceivesWhatsApp => Channels.HasFlag(ReportChannels.WhatsApp);
}

/// <summary>
/// A generated report: one per definition and period (the idempotency key, unique in the database). Holds aggregates and the text written
/// from them, never customer data (docs/REPORTS.md, "Privacy"). Kept <c>Reports:RetentionDays</c> (180 by default) and deleted with the company.
/// </summary>
public class ManagerReport : BaseEntity
{
    public const int HeadlineMaxLength = 200;
    public const int FallbackReasonMaxLength = 40;
    public const int ModelMaxLength = 100;
    public const int DataPackVersionMaxLength = 20;

    public Guid TenantId { get; private set; }

    public Guid DefinitionId { get; private set; }

    /// <summary>The definition's name when it was generated (a later rename does not rewrite history).</summary>
    public string DefinitionName { get; private set; } = string.Empty;

    public ReportFrequency Frequency { get; private set; }

    public DateOnly PeriodFrom { get; private set; }

    /// <summary>Exclusive, like every period of the metrics.</summary>
    public DateOnly PeriodTo { get; private set; }

    /// <summary>Generated while the company was in sandbox mode (its numbers are sandbox numbers).</summary>
    public bool IsSandbox { get; private set; }

    public ReportGenerator Generator { get; private set; }

    /// <summary>Why the template wrote it (<c>ai_disabled</c>, <c>quota</c>, <c>provider_unavailable</c>, <c>invalid_output</c>, ...); null when the model did.</summary>
    public string? FallbackReason { get; private set; }

    public int LlmAttempts { get; private set; }

    public string? Model { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    /// <summary>Estimated USD of the model calls (every attempt, also a rejected one).</summary>
    public decimal EstimatedUsd { get; private set; }

    public string DataPackVersion { get; private set; } = string.Empty;

    /// <summary>The structured content (JSON): indicators, diagnosis, actions.</summary>
    public string Content { get; private set; } = string.Empty;

    /// <summary>The data pack the text was written from (JSON, aggregates only), so a reader can check every number.</summary>
    public string DataPack { get; private set; } = string.Empty;

    /// <summary>The rendered email body (HTML), without the per-recipient links.</summary>
    public string Html { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    /// <summary>One line for the WhatsApp notice and the history list.</summary>
    public string Headline { get; private set; } = string.Empty;

    public DateTime GeneratedAt { get; private set; }

    private ManagerReport()
    {
    }

    public static ManagerReport Create(
        ReportDefinition definition, DateOnly periodFrom, DateOnly periodTo, bool isSandbox, ReportGenerator generator, string? fallbackReason,
        int llmAttempts, string? model, long inputTokens, long outputTokens, decimal estimatedUsd, string dataPackVersion, string content,
        string dataPack, string html, string text, string headline, DateTime generatedAt)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (periodTo <= periodFrom)
        {
            throw new DomainException("El periodo del informe no es válido.");
        }

        return new ManagerReport
        {
            TenantId = definition.TenantId,
            DefinitionId = definition.Id,
            DefinitionName = definition.Name,
            Frequency = definition.Frequency,
            PeriodFrom = periodFrom,
            PeriodTo = periodTo,
            IsSandbox = isSandbox,
            Generator = generator,
            FallbackReason = fallbackReason is null ? null : TextRules.Truncate(fallbackReason, FallbackReasonMaxLength),
            LlmAttempts = llmAttempts,
            Model = model is null ? null : TextRules.Truncate(model, ModelMaxLength),
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedUsd = estimatedUsd,
            DataPackVersion = dataPackVersion,
            Content = content,
            DataPack = dataPack,
            Html = html,
            Text = text,
            Headline = TextRules.Truncate(headline, HeadlineMaxLength),
            GeneratedAt = UtcTime.Normalize(generatedAt),
        };
    }
}

/// <summary>
/// One report to one recipient on one channel: unique per (report, user, channel), so a period is never sent twice to the same person on the
/// same channel. Holds ids, a status and a problem code; never the address or the number (they are the user's, read at send time).
/// </summary>
public class ReportDelivery
{
    public const int ErrorCodeMaxLength = 64;
    public const int ProviderMessageIdMaxLength = 256;

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid TenantId { get; private set; }

    public Guid ReportId { get; private set; }

    public Guid UserId { get; private set; }

    public ReportDeliveryChannel Channel { get; private set; }

    public ReportDeliveryStatus Status { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>When a pending delivery may be attempted (again); also the lease of an attempt in flight.</summary>
    public DateTime? NextAttemptAt { get; private set; }

    public DateTime? LastAttemptAt { get; private set; }

    public DateTime? SentAt { get; private set; }

    /// <summary>A stable code (<c>email_not_configured</c>, <c>no_opt_in</c>, <c>template_unavailable</c>, ...): never provider text.</summary>
    public string? ErrorCode { get; private set; }

    public string? ProviderMessageId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private ReportDelivery()
    {
    }

    public static ReportDelivery Pending(Guid tenantId, Guid reportId, Guid userId, ReportDeliveryChannel channel, DateTime utcNow)
        => new()
        {
            TenantId = tenantId, ReportId = reportId, UserId = userId, Channel = channel, Status = ReportDeliveryStatus.Pending,
            NextAttemptAt = UtcTime.Normalize(utcNow), CreatedAt = UtcTime.Normalize(utcNow),
        };

    public static ReportDelivery Skipped(Guid tenantId, Guid reportId, Guid userId, ReportDeliveryChannel channel, string code, DateTime utcNow)
        => new()
        {
            TenantId = tenantId, ReportId = reportId, UserId = userId, Channel = channel, Status = ReportDeliveryStatus.Skipped,
            ErrorCode = TextRules.Truncate(code, ErrorCodeMaxLength), CreatedAt = UtcTime.Normalize(utcNow),
        };

    /// <summary>Takes the delivery for one attempt: it is leased until <paramref name="leaseUntil"/>, so another worker does not take it meanwhile.</summary>
    public void BeginAttempt(DateTime utcNow, DateTime leaseUntil)
    {
        if (Status != ReportDeliveryStatus.Pending)
        {
            throw new DomainException("Solo se puede intentar una entrega pendiente.");
        }

        Attempts++;
        LastAttemptAt = UtcTime.Normalize(utcNow);
        NextAttemptAt = UtcTime.Normalize(leaseUntil);
    }

    public void MarkSent(DateTime utcNow, string? providerMessageId)
    {
        Status = ReportDeliveryStatus.Sent;
        SentAt = UtcTime.Normalize(utcNow);
        NextAttemptAt = null;
        ErrorCode = null;
        ProviderMessageId = providerMessageId is null ? null : TextRules.Truncate(providerMessageId, ProviderMessageIdMaxLength);
    }

    public void MarkRetry(string code, DateTime nextAttemptAt)
    {
        Status = ReportDeliveryStatus.Pending;
        ErrorCode = TextRules.Truncate(code, ErrorCodeMaxLength);
        NextAttemptAt = UtcTime.Normalize(nextAttemptAt);
    }

    /// <summary>Holds a claimed delivery without spending its attempt (the company is not operating, or email is not configured yet).</summary>
    public void Postpone(string code, DateTime nextAttemptAt)
    {
        Attempts = Math.Max(0, Attempts - 1);
        MarkRetry(code, nextAttemptAt);
    }

    public void MarkFailed(string code)
    {
        Status = ReportDeliveryStatus.Failed;
        ErrorCode = TextRules.Truncate(code, ErrorCodeMaxLength);
        NextAttemptAt = null;
    }
}

/// <summary>
/// The ledger of every report written, stored as soon as it is written (before it is rendered, stored or sent): its kind, who wrote it and what the
/// model cost (docs/REPORTS.md, "Cost and quota"). The model-written reports of the month and their spend are counted here, previews included, so
/// a failure after the call never hides its cost and never calls the model again for the same period. No text, no recipient.
/// </summary>
public class ReportLedgerEntry
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid TenantId { get; private set; }

    public Guid DefinitionId { get; private set; }

    public ReportCompositionKind Kind { get; private set; }

    public ReportFrequency Frequency { get; private set; }

    public DateOnly PeriodFrom { get; private set; }

    public ReportGenerator Generator { get; private set; }

    public string? FallbackReason { get; private set; }

    public int LlmAttempts { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public decimal EstimatedUsd { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private ReportLedgerEntry()
    {
    }

    public static ReportLedgerEntry Record(
        Guid tenantId, Guid definitionId, ReportCompositionKind kind, ReportFrequency frequency, DateOnly periodFrom, ReportGenerator generator,
        string? fallbackReason, int llmAttempts, long inputTokens, long outputTokens, decimal estimatedUsd, DateTime utcNow)
        => new()
        {
            TenantId = tenantId, DefinitionId = definitionId, Kind = kind, Frequency = frequency, PeriodFrom = periodFrom, Generator = generator,
            FallbackReason = fallbackReason is null ? null : TextRules.Truncate(fallbackReason, ManagerReport.FallbackReasonMaxLength),
            LlmAttempts = llmAttempts, InputTokens = inputTokens, OutputTokens = outputTokens, EstimatedUsd = estimatedUsd, CreatedAt = UtcTime.Normalize(utcNow),
        };
}
