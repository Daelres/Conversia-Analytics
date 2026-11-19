using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Application.Reports;
using Conversia.Application.Time;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Conversia.Infrastructure.Reports;

/// <summary>
/// One run of the manager reports job (docs/REPORTS.md, "The job"), safe with several instances:
/// <list type="number">
/// <item><b>Retention</b>: reports older than <c>Reports:RetentionDays</c> are deleted with their deliveries.</item>
/// <item><b>Generation</b>: each active definition of an active company whose latest occurrence is due (within the catch-up window, not before it
/// existed) and has no report yet gets one, in a scope whose tenant is set. The unique (definition, period) index is the idempotency key: a second
/// instance that writes the same report gets a unique violation and drops its copy.</item>
/// <item><b>Delivery</b>: the due pending deliveries are claimed (<c>FOR UPDATE SKIP LOCKED</c>, leased) and sent one by one; each ends sent,
/// retried with backoff, or failed with a stable code.</item>
/// </list>
/// </summary>
/// <summary>What one generation run did: the reports stored, and the periods skipped because they were older than the catch-up window.</summary>
public sealed record ReportGenerationRun(int Stored, IReadOnlyList<(Guid DefinitionId, DateOnly PeriodFrom)> Skipped);

public sealed class ReportJobRunner(IServiceScopeFactory scopes, ReportSettings settings, TimeProvider time, ILogger<ReportJobRunner> logger)
{
    public static readonly EventId ReportPeriodSkipped = new(4307, "ReportPeriodSkipped");

    // The skipped periods already warned about by this instance (one warning per period, not one per run).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, DateOnly), byte> _warned = new();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public static readonly EventId ReportGenerated = new(4303, "ReportGenerated");
    public static readonly EventId ReportJobFailed = new(4305, "ReportJobFailed");
    public static readonly EventId ReportDeliveryFailed = new(4306, "ReportDeliveryFailed");

    /// <summary>How long a claimed delivery is held by the instance sending it.</summary>
    public static readonly TimeSpan DeliveryLease = TimeSpan.FromMinutes(10);

    public const int DeliveryBatch = 20;

    public async Task RunOnceAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        await PurgeAsync(utcNow, cancellationToken);
        await GenerateDueAsync(utcNow, cancellationToken);
        await DeliverDueAsync(utcNow, cancellationToken);
    }

    public async Task<int> PurgeAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var before = utcNow.AddDays(-settings.RetentionDays);
        return await db.ManagerReports.IgnoreQueryFilters().Where(r => r.GeneratedAt < before).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Generates every report that is due now; a period older than the catch-up window is skipped with a warning.</summary>
    public async Task<ReportGenerationRun> GenerateDueAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        List<DueDefinition> definitions;
        TimeZoneInfo zone;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            zone = scope.ServiceProvider.GetRequiredService<TenantTimeZone>().Zone;
            definitions = await (
                    from d in db.ReportDefinitions.IgnoreQueryFilters().AsNoTracking()
                    join t in db.Tenants.AsNoTracking() on d.TenantId equals t.Id
                    where d.IsActive && t.Status == TenantStatus.Active
                    select new DueDefinition(d.Id, d.TenantId, d.Frequency, d.SendDayOfWeek, d.SendHour, d.CreatedAt))
                .ToListAsync(cancellationToken);
        }

        var stored = 0;
        var skipped = new List<(Guid, DateOnly)>();
        foreach (var definition in definitions)
        {
            var occurrence = ReportSchedule.Latest(definition.Frequency, definition.SendDayOfWeek, definition.SendHour, utcNow, zone);
            if (!ReportSchedule.IsDue(occurrence, utcNow, definition.CreatedAt, TimeSpan.FromHours(settings.CatchUpHours)))
            {
                if (occurrence.DueUtc <= utcNow && occurrence.DueUtc >= definition.CreatedAt
                    && !await ExistsAsync(definition.Id, definition.Frequency, occurrence.Period.From, cancellationToken))
                {
                    skipped.Add((definition.Id, occurrence.Period.From));
                    if (_warned.TryAdd((definition.Id, occurrence.Period.From), 0))
                    {
                        logger.LogWarning(ReportPeriodSkipped,
                            "The report of definition {DefinitionId} of tenant {TenantId} for {PeriodFrom} was skipped: it was due at {DueAt:o}, more than {CatchUpHours} hours ago (the job did not run).",
                            definition.Id, definition.TenantId, occurrence.Period.From, occurrence.DueUtc, settings.CatchUpHours);
                    }
                }

                continue;
            }

            try
            {
                if (await GenerateAsync(definition.TenantId, definition.Id, occurrence.Period, Now, cancellationToken) is not null)
                {
                    stored++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One company's failure never stops the others; the next run tries again while the period is within the catch-up window.
                logger.LogError(ReportJobFailed, "The report of definition {DefinitionId} of tenant {TenantId} for {PeriodFrom} could not be generated ({ExceptionType}).",
                    definition.Id, definition.TenantId, occurrence.Period.From, ex.GetType().Name);
            }
        }

        return new ReportGenerationRun(stored, skipped);
    }

    private async Task<bool> ExistsAsync(Guid definitionId, ReportFrequency frequency, DateOnly periodFrom, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ManagerReports.IgnoreQueryFilters()
            .AnyAsync(r => r.DefinitionId == definitionId && r.Frequency == frequency && r.PeriodFrom == periodFrom, cancellationToken);
    }

    /// <summary>Writes and stores the report of one definition and period, with its deliveries. Null when it already exists.</summary>
    public async Task<Guid?> GenerateAsync(Guid tenantId, Guid definitionId, ReportPeriod period, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var definition = await db.ReportDefinitions.AsNoTracking().Include(d => d.Recipients).FirstOrDefaultAsync(d => d.Id == definitionId, cancellationToken);
        if (definition is null
            || await db.ManagerReports.AnyAsync(r => r.DefinitionId == definitionId && r.Frequency == definition.Frequency && r.PeriodFrom == period.From, cancellationToken))
        {
            return null;
        }

        // The writer records the report in the ledger (and its model cost) before anything below can fail; a retry of this period then uses the
        // template instead of calling the model again.
        var draft = await scope.ServiceProvider.GetRequiredService<ReportWriterService>().WriteAsync(tenantId, definition, period, ReportCompositionKind.Scheduled, null, cancellationToken);
        var content = draft.Composition.Content;
        var usage = draft.Composition.Usage;
        var report = ManagerReport.Create(
            definition, period.From, period.To, draft.IsSandbox, draft.Composition.Generator, draft.Composition.FallbackReason, draft.Composition.LlmAttempts,
            draft.Composition.Model, usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens, usage.OutputTokens, draft.Composition.EstimatedUsd,
            draft.Pack.Version, content.ToJson(), draft.Pack.ToJson(), ReportRenderer.Html(content), ReportRenderer.Text(content), content.Headline(), utcNow);

        var users = await db.Users.AsNoTracking().Where(u => definition.Recipients.Select(r => r.UserId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken);
        var deliveries = new List<ReportDelivery>();
        foreach (var recipient in definition.Recipients.Where(r => users.ContainsKey(r.UserId)))
        {
            if (recipient.Channels.HasFlag(ReportChannels.Email))
            {
                deliveries.Add(recipient.EmailUnsubscribedAt is null
                    ? ReportDelivery.Pending(tenantId, report.Id, recipient.UserId, ReportDeliveryChannel.Email, utcNow)
                    : ReportDelivery.Skipped(tenantId, report.Id, recipient.UserId, ReportDeliveryChannel.Email, ReportDeliveryCodes.Unsubscribed, utcNow));
            }

            if (recipient.Channels.HasFlag(ReportChannels.WhatsApp))
            {
                deliveries.Add(users[recipient.UserId].ReceivesReportsOnWhatsApp
                    ? ReportDelivery.Pending(tenantId, report.Id, recipient.UserId, ReportDeliveryChannel.WhatsApp, utcNow)
                    : ReportDelivery.Skipped(tenantId, report.Id, recipient.UserId, ReportDeliveryChannel.WhatsApp, ReportDeliveryCodes.NoOptIn, utcNow));
            }
        }

        db.ManagerReports.Add(report);
        db.ReportDeliveries.AddRange(deliveries);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another instance stored this period first: its report and deliveries stand.
            return null;
        }

        logger.LogInformation(ReportGenerated,
            "Report {ReportId} of definition {DefinitionId} of tenant {TenantId} for {PeriodFrom} was generated by {Generator} (fallback {Fallback}, attempts {Attempts}, estimated USD {EstimatedUsd}, deliveries {Deliveries}).",
            report.Id, definitionId, tenantId, period.From, report.Generator, report.FallbackReason, report.LlmAttempts, report.EstimatedUsd, deliveries.Count);
        return report.Id;
    }

    /// <summary>Sends the due pending deliveries. Returns how many attempts were made.</summary>
    public async Task<int> DeliverDueAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var attempts = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var claimed = await ClaimAsync(utcNow, cancellationToken);
            foreach (var (id, attempt) in claimed)
            {
                await DeliverAsync(id, attempt, cancellationToken);
                attempts++;
            }

            if (claimed.Count < DeliveryBatch)
            {
                break;
            }
        }

        return attempts;
    }

    private async Task<List<(Guid Id, int Attempt)>> ClaimAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DatabaseTime.Truncate(utcNow);
        var due = await db.ReportDeliveries
            .FromSql($"""
                SELECT * FROM "ReportDeliveries" WHERE "Status" = 'Pending' AND "NextAttemptAt" <= {now}
                ORDER BY "NextAttemptAt" LIMIT {DeliveryBatch} FOR UPDATE SKIP LOCKED
                """)
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        foreach (var delivery in due)
        {
            delivery.BeginAttempt(now, now + DeliveryLease);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return [.. due.Select(d => (d.Id, d.Attempts))];
    }

    /// <summary>
    /// One attempt of one claimed delivery, in a scope of its company. <paramref name="claimedAttempt"/> is the attempt number of the claim: when
    /// another instance claimed it again meanwhile (the lease expired), this one stops. Before sending, the lease is renewed with a fresh clock.
    /// </summary>
    public async Task DeliverAsync(Guid deliveryId, int claimedAttempt, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var renewed = await db.ReportDeliveries.IgnoreQueryFilters()
            .Where(d => d.Id == deliveryId && d.Status == ReportDeliveryStatus.Pending && d.Attempts == claimedAttempt)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.NextAttemptAt, DatabaseTime.Truncate(Now + DeliveryLease)), cancellationToken);
        if (renewed == 0)
        {
            return; // sent, failed or claimed again by another instance
        }

        var delivery = await db.ReportDeliveries.IgnoreQueryFilters().FirstAsync(d => d.Id == deliveryId, cancellationToken);
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(delivery.TenantId);
        try
        {
            var outcome = await AttemptAsync(scope.ServiceProvider, db, delivery, cancellationToken);
            Apply(delivery, outcome, Now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ReportDeliveryFailed, "Delivery {DeliveryId} of report {ReportId} failed unexpectedly ({ExceptionType}).", delivery.Id, delivery.ReportId, ex.GetType().Name);
            Apply(delivery, DeliveryOutcome.Retry(ReportDeliveryCodes.Unexpected), Now);
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private void Apply(ReportDelivery delivery, DeliveryOutcome outcome, DateTime utcNow)
    {
        switch (outcome.Kind)
        {
            case OutcomeKind.Sent:
                delivery.MarkSent(utcNow, outcome.ProviderMessageId);
                break;
            case OutcomeKind.Retry when delivery.Attempts < settings.MaxDeliveryAttempts:
                delivery.MarkRetry(outcome.Code!, utcNow + ReportSettings.Backoff(delivery.Attempts));
                break;
            case OutcomeKind.Hold:
                delivery.Postpone(outcome.Code!, utcNow + HoldDelay);
                break;
            default:
                delivery.MarkFailed(outcome.Code ?? ReportDeliveryCodes.Unexpected);
                logger.LogWarning(ReportDeliveryFailed, "Delivery {DeliveryId} of report {ReportId} ({Channel}) failed: {Code} after {Attempts} attempts.",
                    delivery.Id, delivery.ReportId, delivery.Channel, outcome.Code, delivery.Attempts);
                break;
        }
    }

    private async Task<DeliveryOutcome> AttemptAsync(IServiceProvider services, ApplicationDbContext db, ReportDelivery delivery, CancellationToken cancellationToken)
    {
        var report = await db.ManagerReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == delivery.ReportId, cancellationToken);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == delivery.UserId, cancellationToken);
        var recipient = report is null ? null : await db.ReportRecipients.AsNoTracking().FirstOrDefaultAsync(r => r.DefinitionId == report.DefinitionId && r.UserId == delivery.UserId, cancellationToken);
        if (report is null || user is null || recipient is null)
        {
            return DeliveryOutcome.Fail(ReportDeliveryCodes.RecipientRemoved);
        }

        var content = ReportContent.FromJson(report.Content);
        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == delivery.TenantId).Select(t => new { t.Name, t.SandboxMode, t.Status }).FirstAsync(cancellationToken);
        var expired = Now - report.GeneratedAt > TimeSpan.FromHours(settings.CatchUpHours);
        if (tenant.Status != TenantStatus.Active)
        {
            // A company that does not operate (pending, paused, suspended, cancelled) sends nothing; its deliveries wait for it within the
            // catch-up window, then fail: a manager never gets a stale report days late.
            return expired ? DeliveryOutcome.Fail(ReportDeliveryCodes.CompanyInactive) : DeliveryOutcome.Holding(ReportDeliveryCodes.CompanyInactive);
        }

        var outcome = delivery.Channel == ReportDeliveryChannel.Email
            ? await EmailAsync(services, user, recipient, report, content, tenant.Name, delivery.Id, cancellationToken)
            : await WhatsAppAsync(services, db, user, recipient, report, content, tenant.SandboxMode, cancellationToken);

        // No email provider yet: kept for the catch-up window, so it goes out once email is configured.
        return outcome.Kind == OutcomeKind.Hold && expired ? DeliveryOutcome.Fail(outcome.Code!) : outcome;
    }

    private async Task<DeliveryOutcome> EmailAsync(
        IServiceProvider services, User user, ReportRecipient recipient, ManagerReport report, ReportContent content, string company, Guid deliveryId,
        CancellationToken cancellationToken)
    {
        if (!recipient.ReceivesEmail)
        {
            return DeliveryOutcome.Fail(ReportDeliveryCodes.Unsubscribed);
        }

        var token = ReportUnsubscribeToken.Create(recipient.Id, services.GetRequiredService<ReportUnsubscribeKey>().Key);
        var message = ReportEmailComposer.Compose(
            user.Email, company, content, settings.ReportUrl(report.Id), settings.UnsubscribeUrl(token), settings.OneClickUnsubscribeUrl(token), $"report-delivery-{deliveryId:N}");
        var result = await services.GetRequiredService<IEmailSender>().SendAsync(message, cancellationToken);
        return result.Status switch
        {
            EmailSendStatus.Sent => DeliveryOutcome.Done(result.MessageId),
            EmailSendStatus.TransientFailure => DeliveryOutcome.Retry(result.ErrorCode ?? "smtp_unavailable"),
            EmailSendStatus.NotConfigured => DeliveryOutcome.Holding(ReportDeliveryCodes.EmailNotConfigured),
            // The server may have accepted it: not repeated, so nobody gets the report twice (docs/REPORTS.md, "Duplicates").
            EmailSendStatus.UnknownOutcome => DeliveryOutcome.Fail(result.ErrorCode ?? "smtp_unknown_outcome"),
            _ => DeliveryOutcome.Fail(result.ErrorCode ?? "message_rejected"),
        };
    }

    private async Task<DeliveryOutcome> WhatsAppAsync(
        IServiceProvider services, ApplicationDbContext db, User user, ReportRecipient recipient, ManagerReport report, ReportContent content, bool sandboxMode,
        CancellationToken cancellationToken)
    {
        // The person's current choice and consent, at send time (like the email's unsubscribe).
        if (!recipient.ReceivesWhatsApp)
        {
            return DeliveryOutcome.Fail(ReportDeliveryCodes.ChannelRemoved);
        }

        if (!user.ReceivesReportsOnWhatsApp)
        {
            return DeliveryOutcome.Fail(ReportDeliveryCodes.NoOptIn);
        }

        var definition = await db.ReportDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == report.DefinitionId, cancellationToken);
        if (definition?.WhatsAppChannelId is not { } channelId)
        {
            return DeliveryOutcome.Fail(ReportDeliveryCodes.TemplateUnavailable);
        }

        var channel = await db.WhatsAppChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null || !channel.IsActive)
        {
            return DeliveryOutcome.Fail(ReportDeliveryCodes.ChannelUnavailable);
        }

        var template = await db.MessageTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ChannelId == channelId && t.Name == definition.WhatsAppTemplateName && t.Language == definition.WhatsAppTemplateLanguage, cancellationToken);
        if (template is null || !template.CanBeSent || template.Category != TemplateCategory.Utility || template.ParameterCount != ReportStore.TemplateParameters)
        {
            return DeliveryOutcome.Fail(ReportDeliveryCodes.TemplateUnavailable);
        }

        if (!sandboxMode && !channel.HasProviderApiKey)
        {
            return DeliveryOutcome.Fail(ReportDeliveryCodes.CredentialsMissing);
        }

        string[] values =
        [
            TemplateValue(ReportText.PeriodLabel(report.Frequency, report.PeriodFrom, report.PeriodTo)),
            TemplateValue(content.Headline()),
            settings.ReportUrl(report.Id),
        ];
        var parameters = template.ParameterNames.Select((name, i) => new WhatsAppTemplateParameter(template.NamedParameters ? name : null, values[i])).ToList();
        var sender = services.GetRequiredService<IWhatsAppMessageSenderResolver>().Resolve(channel, sandboxMode);
        var result = await sender.SendTemplateAsync(new WhatsAppTemplateMessage(channel, user.ReportsWhatsAppNumber!, template.Name, template.Language, parameters), cancellationToken);
        return result.Status switch
        {
            WhatsAppSendStatus.Sent => DeliveryOutcome.Done(result.ProviderMessageId),
            // Not sent: safe to try again later.
            WhatsAppSendStatus.LimitReached => DeliveryOutcome.Retry("provider_limit_reached"),
            // Whether it left is unknown after a timeout (docs/MESSAGING.md): like a manual send, it is not repeated, so a manager never gets it twice.
            WhatsAppSendStatus.TransientFailure => DeliveryOutcome.Fail("provider_unreachable"),
            WhatsAppSendStatus.InvalidCredentials => DeliveryOutcome.Fail("provider_credentials_invalid"),
            WhatsAppSendStatus.RecipientNotAllowed => DeliveryOutcome.Fail("recipient_not_allowed"),
            _ => DeliveryOutcome.Fail("provider_rejected"),
        };
    }

    // A template value is one line of at most 500 characters with no tabs or runs of spaces (docs/MESSAGING.md, "Templates").
    internal static string TemplateValue(string text)
    {
        var line = string.Join(' ', text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return TextRules.Truncate(line, MessageTemplate.ParameterMaxLength);
    }

    private sealed record DueDefinition(Guid Id, Guid TenantId, ReportFrequency Frequency, int SendDayOfWeek, int SendHour, DateTime CreatedAt);

    /// <summary>How long a held delivery waits before it is looked at again.</summary>
    public static readonly TimeSpan HoldDelay = TimeSpan.FromHours(1);

    private enum OutcomeKind
    {
        Sent,
        Retry,
        Failed,
        Hold,
    }

    private sealed record DeliveryOutcome(OutcomeKind Kind, string? Code, string? ProviderMessageId)
    {
        public static DeliveryOutcome Done(string? id) => new(OutcomeKind.Sent, null, id);

        public static DeliveryOutcome Retry(string code) => new(OutcomeKind.Retry, code, null);

        public static DeliveryOutcome Fail(string code) => new(OutcomeKind.Failed, code, null);

        public static DeliveryOutcome Holding(string code) => new(OutcomeKind.Hold, code, null);
    }
}

/// <summary>The stable codes of a delivery that was not sent (the Web words them).</summary>
public static class ReportDeliveryCodes
{
    public const string Unsubscribed = "unsubscribed";
    public const string NoOptIn = "no_opt_in";
    public const string RecipientRemoved = "recipient_removed";
    public const string EmailNotConfigured = "email_not_configured";
    public const string TemplateUnavailable = "template_unavailable";
    public const string ChannelUnavailable = "channel_unavailable";
    public const string CredentialsMissing = "credentials_missing";
    public const string Unexpected = "unexpected_error";
    public const string CompanyInactive = "company_inactive";
    public const string ChannelRemoved = "channel_removed";
}

/// <summary><c>Reports:Job:*</c> drives this: off in the test hosts (they call the runner with the time they choose).</summary>
public sealed class ReportJobService(ReportJobRunner runner, ReportSettings settings, TimeProvider time, ILogger<ReportJobService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunOnceAsync(time.GetUtcNow().UtcDateTime, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ReportJobRunner.ReportJobFailed, "The manager reports job failed ({ExceptionType}); it runs again in {Seconds} seconds.", ex.GetType().Name, settings.JobIntervalSeconds);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.JobIntervalSeconds), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
