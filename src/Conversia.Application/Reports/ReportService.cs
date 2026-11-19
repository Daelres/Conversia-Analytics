using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Reports;
using Conversia.Application.Exceptions;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.Reports;

/// <summary>The manager reports of the caller's company (docs/REPORTS.md): definitions, preview, history, opt-in and unsubscribe.</summary>
public interface IReportService
{
    Task<IReadOnlyList<ReportDefinitionResponse>> ListDefinitionsAsync(CancellationToken cancellationToken = default);

    Task<ReportDefinitionResponse> GetDefinitionAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ReportDefinitionResponse> CreateDefinitionAsync(Guid tenantId, ReportDefinitionRequest request, Guid actorUserId, CancellationToken cancellationToken = default);

    Task<ReportDefinitionResponse> UpdateDefinitionAsync(Guid tenantId, Guid id, ReportDefinitionRequest request, Guid actorUserId, CancellationToken cancellationToken = default);

    Task DeleteDefinitionAsync(Guid tenantId, Guid id, Guid actorUserId, CancellationToken cancellationToken = default);

    Task<ReportOptionsResponse> GetOptionsAsync(CancellationToken cancellationToken = default);

    Task<ReportPreviewResponse> PreviewAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);

    Task<PagedResponse<ReportSummaryResponse>> ListReportsAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    /// <param name="canReadAll">The caller sees the company's metrics or manages the reports; otherwise only reports it receives are readable.</param>
    Task<ReportResponse> GetReportAsync(Guid id, Guid viewerUserId, bool canReadAll, CancellationToken cancellationToken = default);

    Task<ReportWhatsAppOptInResponse> GetWhatsAppOptInAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<ReportWhatsAppOptInResponse> OptInAsync(Guid tenantId, Guid userId, ReportWhatsAppOptInRequest request, CancellationToken cancellationToken = default);

    Task<ReportWhatsAppOptInResponse> VerifyWhatsAppAsync(Guid tenantId, Guid userId, ReportWhatsAppVerifyRequest request, CancellationToken cancellationToken = default);

    Task<ReportWhatsAppOptInResponse> OptOutAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Null when the token is not valid (the caller answers the same way as for a valid one it cannot find).</summary>
    Task<ReportUnsubscribeResponse?> UnsubscribeAsync(string? token, CancellationToken cancellationToken = default);
}

/// <summary>
/// The use cases of the manager reports. The changes a company makes are its trail as structured log lines (event 4301; there is no tenant
/// audit table, as for a contact suppression): who, which definition, what kind of change, never an address or a number.
/// </summary>
public sealed class ReportService(
    IReportStore store, ReportWriterService writer, ReportSettings settings, ReportUnsubscribeKey unsubscribeKey, IReportWhatsAppCodeSender codeSender,
    TimeProvider time, ILogger<ReportService> logger)
    : IReportService
{
    public static readonly EventId ReportTrail = new(4301, "ReportTrail");

    public const string UnknownUserMessage = "Una de las personas elegidas no pertenece a la empresa.";
    public const string UnknownBranchMessage = "Una de las sucursales elegidas no pertenece a la empresa.";
    public const string TemplateMessage =
        "La plantilla elegida no está disponible: debe ser una plantilla de utilidad aprobada, de un número activo, con tres campos (periodo, resumen y enlace).";
    public const string SectionMessage = "Una de las partes del análisis elegidas no existe.";

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<ReportDefinitionResponse>> ListDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        var users = (await store.ListUsersAsync(cancellationToken)).ToDictionary(u => u.Id);
        return [.. (await store.ListDefinitionsAsync(cancellationToken)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).Select(d => ToResponse(d, users))];
    }

    public async Task<ReportDefinitionResponse> GetDefinitionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definition = await store.FindDefinitionAsync(id, cancellationToken) ?? throw new ReportNotFoundException();
        return ToResponse(definition, (await store.ListUsersAsync(cancellationToken)).ToDictionary(u => u.Id));
    }

    public async Task<ReportDefinitionResponse> CreateDefinitionAsync(Guid tenantId, ReportDefinitionRequest request, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        if (await store.CountDefinitionsAsync(cancellationToken) >= settings.MaxDefinitionsPerCompany)
        {
            throw new ReportLimitException(settings.MaxDefinitionsPerCompany);
        }

        var (definitionSettings, users) = await ValidateAsync(request, cancellationToken);
        var definition = ReportDefinition.Create(tenantId, definitionSettings, settings.MaxRecipientsPerDefinition);
        store.AddDefinition(definition);
        await store.SaveChangesAsync(cancellationToken);
        Trail("created", tenantId, definition, actorUserId);
        return ToResponse(definition, users);
    }

    public async Task<ReportDefinitionResponse> UpdateDefinitionAsync(Guid tenantId, Guid id, ReportDefinitionRequest request, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var definition = await store.FindDefinitionAsync(id, cancellationToken) ?? throw new ReportNotFoundException();
        var (definitionSettings, users) = await ValidateAsync(request, cancellationToken);
        definition.Update(definitionSettings, settings.MaxRecipientsPerDefinition);
        await store.SaveChangesAsync(cancellationToken);
        Trail("updated", tenantId, definition, actorUserId);
        return ToResponse(definition, users);
    }

    public async Task DeleteDefinitionAsync(Guid tenantId, Guid id, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var definition = await store.FindDefinitionAsync(id, cancellationToken) ?? throw new ReportNotFoundException();
        await store.DeleteDefinitionAsync(definition, cancellationToken);
        Trail("deleted", tenantId, definition, actorUserId);
    }

    public async Task<ReportOptionsResponse> GetOptionsAsync(CancellationToken cancellationToken = default)
    {
        var users = await store.ListUsersAsync(cancellationToken);
        return new ReportOptionsResponse(
            [.. users.OrderBy(u => u.Email, StringComparer.OrdinalIgnoreCase).Select(u => new ReportUserOption(u.Id, u.Email, u.WhatsAppOptedIn))],
            await store.ListBranchesAsync(cancellationToken),
            await store.ListTemplateOptionsAsync(cancellationToken),
            [.. SectionsInOrder.Select(s => new ReportSectionOption(s.ToString(), ReportText.Section(s)))],
            await store.CountDefinitionsAsync(cancellationToken),
            settings.MaxDefinitionsPerCompany,
            settings.MaxRecipientsPerDefinition);
    }

    public async Task<ReportPreviewResponse> PreviewAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
    {
        var definition = await store.FindDefinitionAsync(id, cancellationToken) ?? throw new ReportNotFoundException();
        var period = ReportSchedule.LastCompleted(definition.Frequency, Now, writer.Zone);
        // Previews count against the month's model-written reports and spend (the ledger); past the company's daily budget the template writes them.
        var today = ReportSchedule.ToUtc(ReportSchedule.LocalDate(Now, writer.Zone), 0, writer.Zone);
        var blocked = await store.CountPreviewsSinceAsync(today, cancellationToken) >= settings.PreviewsPerCompanyPerDay ? ReportFallbackReasons.PreviewBudget : null;
        var draft = await writer.WriteAsync(tenantId, definition, period, ReportCompositionKind.Preview, blocked, cancellationToken);
        logger.LogInformation(ReportTrail, "Report definition {DefinitionId} of tenant {TenantId} was previewed (generator {Generator}, fallback {Fallback}, estimated USD {EstimatedUsd}).",
            definition.Id, tenantId, draft.Composition.Generator, draft.Composition.FallbackReason, draft.Composition.EstimatedUsd);
        return new ReportPreviewResponse(Period(definition.Frequency, period), draft.Composition.Generator.ToString(), draft.Composition.FallbackReason, draft.Composition.Content);
    }

    public Task<PagedResponse<ReportSummaryResponse>> ListReportsAsync(int page, int pageSize, CancellationToken cancellationToken = default)
        => store.ListReportsAsync(Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), cancellationToken);

    public async Task<ReportResponse> GetReportAsync(Guid id, Guid viewerUserId, bool canReadAll, CancellationToken cancellationToken = default)
    {
        var found = await store.GetReportAsync(id, cancellationToken) ?? throw new ReportNotFoundException();
        var report = found.Report;
        if (!canReadAll && !await store.IsRecipientAsync(report.DefinitionId, viewerUserId, cancellationToken))
        {
            throw new ReportNotFoundException();
        }

        var company = (await store.GetTenantAsync(report.TenantId, cancellationToken))?.Name ?? string.Empty;
        IReadOnlyList<ReportDeliveryResponse> deliveries = canReadAll
            ? [.. found.Deliveries.Select(d => new ReportDeliveryResponse(d.Email, d.Delivery.Channel.ToString(), d.Delivery.Status.ToString(), d.Delivery.Attempts, d.Delivery.ErrorCode, d.Delivery.SentAt))]
            : [];
        return new ReportResponse(
            report.Id, report.DefinitionId, report.DefinitionName, company, report.Frequency.ToString(),
            Period(report.Frequency, new ReportPeriod(report.PeriodFrom, report.PeriodTo)), report.Generator.ToString(), report.IsSandbox, report.GeneratedAt,
            ReportContent.FromJson(report.Content), deliveries);
    }

    public async Task<ReportWhatsAppOptInResponse> GetWhatsAppOptInAsync(Guid userId, CancellationToken cancellationToken = default)
        => OptIn(await store.FindUserAsync(userId, cancellationToken) ?? throw new UserNotFoundException());

    public async Task<ReportWhatsAppOptInResponse> OptInAsync(Guid tenantId, Guid userId, ReportWhatsAppOptInRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await store.FindUserAsync(userId, cancellationToken) ?? throw new UserNotFoundException();
        user.OptInToReportsOnWhatsApp(request.PhoneNumber, request.Consent, Now);
        var codeSent = false;
        if (!user.ReceivesReportsOnWhatsApp)
        {
            // The number must prove it is the person's: a one-time code goes to it through the company's report template; until it comes back,
            // nothing is sent there. Only its hash is kept.
            var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            user.IssueReportsWhatsAppCode(CodeHash(userId, code), Now.Add(CodeLifetime));
            await store.SaveChangesAsync(cancellationToken);
            codeSent = await codeSender.SendAsync(tenantId, user.ReportsWhatsAppNumber!, code, cancellationToken);
        }
        else
        {
            await store.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(ReportTrail, "User {UserId} of tenant {TenantId} accepted to receive the reports on WhatsApp at {OptedInAt:o} (code sent {CodeSent}).",
            userId, tenantId, user.ReportsWhatsAppOptInAt, codeSent);
        return OptIn(user) with { CodeSent = codeSent };
    }

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);

    public const string WrongCodeMessage = "El código no es válido o ya venció. Pide uno nuevo guardando otra vez tu número.";

    public async Task<ReportWhatsAppOptInResponse> VerifyWhatsAppAsync(Guid tenantId, Guid userId, ReportWhatsAppVerifyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await store.FindUserAsync(userId, cancellationToken) ?? throw new UserNotFoundException();
        var verified = user.VerifyReportsWhatsApp(CodeHash(userId, request.Code?.Trim() ?? string.Empty), Now);
        await store.SaveChangesAsync(cancellationToken); // a wrong attempt counts
        if (!verified)
        {
            throw new DomainException(WrongCodeMessage);
        }

        logger.LogInformation(ReportTrail, "User {UserId} of tenant {TenantId} verified the WhatsApp number of the reports.", userId, tenantId);
        return OptIn(user);
    }

    private static string CodeHash(Guid userId, string code)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"report-whatsapp-code:{userId:N}:{code}")));

    public async Task<ReportWhatsAppOptInResponse> OptOutAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await store.FindUserAsync(userId, cancellationToken) ?? throw new UserNotFoundException();
        user.OptOutOfReportsOnWhatsApp(Now);
        await store.SaveChangesAsync(cancellationToken);
        logger.LogInformation(ReportTrail, "User {UserId} of tenant {TenantId} withdrew the WhatsApp consent of the reports.", userId, tenantId);
        return OptIn(user);
    }

    public async Task<ReportUnsubscribeResponse?> UnsubscribeAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!ReportUnsubscribeToken.TryRead(token, unsubscribeKey.Key, out var recipientId)
            || await store.FindRecipientForUnsubscribeAsync(recipientId, cancellationToken) is not { } found)
        {
            return null;
        }

        found.Recipient.UnsubscribeEmail(Now);
        await store.SaveChangesAsync(cancellationToken);
        logger.LogInformation(ReportTrail, "User {UserId} of tenant {TenantId} unsubscribed from the emails of report definition {DefinitionId}.",
            found.Recipient.UserId, found.Recipient.TenantId, found.Recipient.DefinitionId);
        return new ReportUnsubscribeResponse(found.DefinitionName);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static readonly ReportSections[] SectionsInOrder =
        [ReportSections.Headline, ReportSections.Conversion, ReportSections.DropOff, ReportSections.LossReasons, ReportSections.Response, ReportSections.AiCoverage];

    private async Task<(ReportDefinitionSettings Settings, Dictionary<Guid, ReportUserFact> Users)> ValidateAsync(ReportDefinitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var frequency = Enum.Parse<ReportFrequency>(request.Frequency!.Trim(), ignoreCase: true);
        var sections = ReportSections.None;
        foreach (var name in request.Sections ?? [])
        {
            if (!Enum.TryParse<ReportSections>(name?.Trim(), ignoreCase: true, out var section) || !SectionsInOrder.Contains(section))
            {
                throw new DomainException(SectionMessage);
            }

            sections |= section;
        }

        var users = (await store.ListUsersAsync(cancellationToken)).ToDictionary(u => u.Id);
        if ((request.Recipients ?? []).Any(r => r.UserId is not { } id || !users.ContainsKey(id)))
        {
            throw new DomainException(UnknownUserMessage);
        }

        var branches = (await store.ListBranchesAsync(cancellationToken)).Select(b => b.Id).ToHashSet();
        if ((request.BranchIds ?? []).Any(b => !branches.Contains(b)))
        {
            throw new DomainException(UnknownBranchMessage);
        }

        if (request.WhatsAppChannelId is { } channelId && !string.IsNullOrWhiteSpace(request.WhatsAppTemplateName))
        {
            var templates = await store.ListTemplateOptionsAsync(cancellationToken);
            if (!templates.Any(t => t.ChannelId == channelId && t.Name == request.WhatsAppTemplateName.Trim()
                    && string.Equals(t.Language, request.WhatsAppTemplateLanguage?.Trim(), StringComparison.Ordinal)))
            {
                throw new DomainException(TemplateMessage);
            }
        }

        var recipients = (request.Recipients ?? [])
            .Select(r => new ReportRecipientSetting(r.UserId!.Value, (r.Email ? ReportChannels.Email : ReportChannels.None) | (r.WhatsApp ? ReportChannels.WhatsApp : ReportChannels.None)))
            .ToList();
        return (new ReportDefinitionSettings(
            request.Name ?? string.Empty, request.IsActive, frequency, request.SendDayOfWeek, request.SendHour, request.BranchIds ?? [], sections, recipients,
            request.WhatsAppChannelId, request.WhatsAppTemplateName, request.WhatsAppTemplateLanguage), users);
    }

    private ReportDefinitionResponse ToResponse(ReportDefinition definition, IReadOnlyDictionary<Guid, ReportUserFact> users)
    {
        var next = ReportSchedule.Next(definition.Frequency, definition.SendDayOfWeek, definition.SendHour, Now, writer.Zone);
        return new ReportDefinitionResponse(
            definition.Id, definition.Name, definition.IsActive, definition.Frequency.ToString(), definition.SendDayOfWeek, definition.SendHour, definition.BranchIds,
            [.. SectionsInOrder.Where(s => definition.Sections.HasFlag(s)).Select(s => s.ToString())],
            [.. definition.Recipients.Select(r => new ReportRecipientResponse(
                r.UserId, users.TryGetValue(r.UserId, out var user) ? user.Email : string.Empty, r.Channels.HasFlag(ReportChannels.Email), r.ReceivesWhatsApp,
                r.EmailUnsubscribedAt, user?.WhatsAppOptedIn ?? false))],
            definition.WhatsAppChannelId, definition.WhatsAppTemplateName, definition.WhatsAppTemplateLanguage, next.DueUtc,
            Period(definition.Frequency, next.Period), definition.CreatedAt);
    }

    public static ReportPeriodResponse Period(ReportFrequency frequency, ReportPeriod period)
        => new(period.From, period.To, ReportText.PeriodLabel(frequency, period.From, period.To));

    private static ReportWhatsAppOptInResponse OptIn(User user)
        => new(user.ReportsWhatsAppOptInAt is not null, user.ReportsWhatsAppNumber is { } n ? "+" + n : null, user.ReportsWhatsAppOptInAt, user.ReportsWhatsAppOptOutAt,
            user.ReceivesReportsOnWhatsApp, user.ReportsWhatsAppVerifiedAt);

    private void Trail(string action, Guid tenantId, ReportDefinition definition, Guid actorUserId)
        => logger.LogInformation(ReportTrail,
            "Report definition {DefinitionId} of tenant {TenantId} was {Action} by user {UserId} (active {Active}, frequency {Frequency}, recipients {Recipients}, branches {Branches}, sections {Sections}).",
            definition.Id, tenantId, action, actorUserId, definition.IsActive, definition.Frequency, definition.Recipients.Count, definition.BranchIds.Length, definition.Sections);
}
