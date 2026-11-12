using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Monitoring;

/// <summary>
/// The platform administrator's monitoring counts (docs/PLATFORM_ADMIN.md, "Monitoring"). Each <c>IgnoreQueryFilters()</c> here answers a
/// platform question across companies (or for one company named explicitly) and projects counts, times, codes and money only: never a
/// message text, a payload (<c>WebhookEvents.RawPayload</c> is never selected), a contact, a conversation or an error message. Every list is
/// one grouped query (no query per company); the indexes it relies on are <c>WebhookEvents (ReceivedAt)</c> and <c>(TenantId, ReceivedAt)</c>,
/// <c>Messages (TenantId, SentAt)</c>, <c>Conversations (TenantId, StartedAt)</c> and <c>(TenantId, ClosedAt)</c>, <c>ErrorLogs (OccurredAt)</c>.
/// </summary>
public sealed class PlatformMonitoringQueries : IPlatformMonitoringQueries
{
    /// <summary>Each monitoring statement's limit: a slow count must not hold a connection of the API for long.</summary>
    public const int CommandTimeoutSeconds = 10;

    private const int TopErrors = 10;
    private const int SendDays = 7;
    private const int AiMonths = 3;

    private readonly ApplicationDbContext _db;
    private readonly ClassificationOptions _classification;

    public PlatformMonitoringQueries(ApplicationDbContext db, ClassificationOptions classification)
    {
        _db = db;
        _classification = classification;
    }

    public Task<PlatformActivity> GetPlatformAsync(MonitoringRequest request, CancellationToken cancellationToken = default)
        => WithShortTimeoutAsync(() => ReadPlatformAsync(request, cancellationToken));

    public Task<TenantActivity> GetTenantAsync(Guid tenantId, MonitoringRequest request, CancellationToken cancellationToken = default)
        => WithShortTimeoutAsync(() => ReadTenantAsync(tenantId, request, cancellationToken));

    // The scope's context is shared with the rest of the request: its own timeout is put back afterwards.
    private async Task<T> WithShortTimeoutAsync<T>(Func<Task<T>> read)
    {
        var previous = _db.Database.GetCommandTimeout();
        _db.Database.SetCommandTimeout(CommandTimeoutSeconds);
        try
        {
            return await read();
        }
        finally
        {
            _db.Database.SetCommandTimeout(previous);
        }
    }

    private async Task<PlatformActivity> ReadPlatformAsync(MonitoringRequest request, CancellationToken cancellationToken)
    {
        var backlog = await BacklogAsync(null, cancellationToken);
        var deliveries = await DeliveriesAsync(null, request.Now, cancellationToken);

        var ai = await _db.AiUsageMonths.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Month == request.Month)
            .GroupBy(u => u.IsSandbox)
            .Select(g => new AiUsageTotals(request.Month, g.Key, g.Sum(u => u.ConversationsAnalyzed), g.Sum(u => u.Calls), g.Sum(u => u.FailedCalls),
                g.Sum(u => u.InputTokens), g.Sum(u => u.OutputTokens), g.Sum(u => u.CacheReadTokens), g.Sum(u => u.CacheWriteTokens), g.Sum(u => u.EstimatedUsd)))
            .ToListAsync(cancellationToken);

        var (errors, topErrors) = await ErrorsAsync(null, request.Now, cancellationToken);
        var sends = await SendsAsync(null, request, cancellationToken);

        // Companies that should be receiving and are not: Active, out of sandbox mode, with real lines on, and no real delivery in the window.
        // One statement each for the list and its total (COUNT(*) OVER () is computed before the LIMIT).
        var cutoff = request.Now.AddHours(-request.SilentLineHours);
        var active = nameof(TenantStatus.Active);
        var silent = await _db.Database.SqlQuery<SilentRow>($"""
            SELECT s."Id" AS "TenantId", s."Name", s."ActiveLines", s."Last" AS "LastDeliveryAt", COUNT(*) OVER ()::int AS "Total"
            FROM (
                SELECT t."Id", t."Name",
                       (SELECT COUNT(*) FROM "WhatsAppChannels" c WHERE c."TenantId" = t."Id" AND c."IsActive")::int AS "ActiveLines",
                       (SELECT MAX(e."ReceivedAt") FROM "WebhookEvents" e WHERE e."TenantId" = t."Id" AND NOT e."IsSandbox") AS "Last"
                FROM "Tenants" t
                WHERE t."Status" = {active} AND NOT t."SandboxMode"
            ) s
            WHERE s."ActiveLines" > 0 AND (s."Last" IS NULL OR s."Last" < {cutoff})
            ORDER BY s."Last" NULLS FIRST, s."Name"
            LIMIT {request.ListLimit}
            """).ToListAsync(cancellationToken);

        // Companies whose AI is on and that used up their quota or ceiling this month (real usage only).
        var limited = await _db.Database.SqlQuery<LimitedRow>($"""
            SELECT t."Id" AS "TenantId", t."Name", u."ConversationsAnalyzed" AS "Conversations", q."Quota", u."EstimatedUsd",
                   (q."Quota" * {request.AiUsdPerQuotaUnit}) AS "CeilingUsd", COUNT(*) OVER ()::int AS "Total"
            FROM "Tenants" t
            JOIN "AiUsageMonths" u ON u."TenantId" = t."Id" AND u."Month" = {request.Month} AND NOT u."IsSandbox"
            CROSS JOIN LATERAL (SELECT COALESCE(t."AiMonthlyConversationQuota", {request.DefaultAiQuota}) AS "Quota") q
            WHERE t."AiEnabled" AND (q."Quota" <= 0 OR u."ConversationsAnalyzed" >= q."Quota" OR u."EstimatedUsd" >= q."Quota" * {request.AiUsdPerQuotaUnit})
            ORDER BY u."EstimatedUsd" DESC, t."Name"
            LIMIT {request.ListLimit}
            """).ToListAsync(cancellationToken);

        var silentList = silent.Select(x => new SilentTenant(x.TenantId, x.Name, x.ActiveLines, x.LastDeliveryAt)).ToList();
        var limitedList = limited.Select(x => new AiLimitedTenant(x.TenantId, x.Name, x.Conversations, x.Quota, x.EstimatedUsd, x.CeilingUsd)).ToList();
        var silentTotal = silent.FirstOrDefault()?.Total ?? 0;
        var limitedTotal = limited.FirstOrDefault()?.Total ?? 0;

        return new PlatformActivity(backlog, deliveries, ai, errors, topErrors, sends, silentTotal, silentList, limitedTotal, limitedList);
    }

    private async Task<TenantActivity> ReadTenantAsync(Guid tenantId, MonitoringRequest request, CancellationToken cancellationToken)
    {
        var backlog = await BacklogAsync(tenantId, cancellationToken);
        var deliveries = await DeliveriesAsync(tenantId, request.Now, cancellationToken);
        var lastReal = await _db.WebhookEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsSandbox).MaxAsync(e => (DateTime?)e.ReceivedAt, cancellationToken);

        var fromMonth = request.Month.AddMonths(-(AiMonths - 1));
        var ai = await _db.AiUsageMonths.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.Month >= fromMonth)
            .Select(u => new AiUsageTotals(u.Month, u.IsSandbox, u.ConversationsAnalyzed, u.Calls, u.FailedCalls, u.InputTokens, u.OutputTokens, u.CacheReadTokens,
                u.CacheWriteTokens, u.EstimatedUsd))
            .ToListAsync(cancellationToken);

        var (errors, topErrors) = await ErrorsAsync(tenantId, request.Now, cancellationToken);
        var sends = await SendsAsync(tenantId, request, cancellationToken);

        // Counts per local day: message directions and conversation starts and closes. Text is never read; the sandbox flag of the
        // conversation keeps test traffic apart.
        var inbound = nameof(MessageDirection.Inbound);
        var outbound = nameof(MessageDirection.Outbound);
        var days = await _db.Database.SqlQuery<DailyRow>($"""
            SELECT d."Day", d."IsSandbox", SUM(d."In")::int AS "MessagesIn", SUM(d."Out")::int AS "MessagesOut",
                   SUM(d."Opened")::int AS "Opened", SUM(d."Closed")::int AS "Closed"
            FROM (
                SELECT (m."SentAt" AT TIME ZONE {request.TimeZoneId})::date AS "Day", c."IsSandbox",
                       CASE WHEN m."Direction" = {inbound} THEN 1 ELSE 0 END AS "In", CASE WHEN m."Direction" = {outbound} THEN 1 ELSE 0 END AS "Out",
                       0 AS "Opened", 0 AS "Closed"
                FROM "Messages" m JOIN "Conversations" c ON c."Id" = m."ConversationId"
                WHERE m."TenantId" = {tenantId} AND m."SentAt" >= {request.FirstDayStartUtc}
                UNION ALL
                SELECT ("StartedAt" AT TIME ZONE {request.TimeZoneId})::date, "IsSandbox", 0, 0, 1, 0
                FROM "Conversations" WHERE "TenantId" = {tenantId} AND "StartedAt" >= {request.FirstDayStartUtc}
                UNION ALL
                SELECT ("ClosedAt" AT TIME ZONE {request.TimeZoneId})::date, "IsSandbox", 0, 0, 0, 1
                FROM "Conversations" WHERE "TenantId" = {tenantId} AND "ClosedAt" >= {request.FirstDayStartUtc}
            ) d
            GROUP BY d."Day", d."IsSandbox"
            """).ToListAsync(cancellationToken);

        return new TenantActivity(
            backlog, deliveries, lastReal, ai, errors, topErrors, sends,
            days.Select(d => new DailyActivity(d.Day, d.IsSandbox, d.MessagesIn, d.MessagesOut, d.Opened, d.Closed)).ToList());
    }

    private sealed class DailyRow
    {
        public DateOnly Day { get; set; }

        public bool IsSandbox { get; set; }

        public int MessagesIn { get; set; }

        public int MessagesOut { get; set; }

        public int Opened { get; set; }

        public int Closed { get; set; }
    }

    private async Task<BacklogCounts> BacklogAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        // Platform-wide, only the work a worker will take (deliveries: the companies that receive; classification: Active ones); for one
        // company, whatever waits for it.
        var events = _db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().Where(e => e.Status == WebhookEventStatus.Pending);
        var flagged = _db.Conversations.IgnoreQueryFilters().AsNoTracking().Where(c => c.ClassificationDirty);
        if (tenantId is { } id)
        {
            events = events.Where(e => e.TenantId == id);
            flagged = flagged.Where(c => c.TenantId == id);
        }
        else
        {
            events = events.Where(e => _db.Tenants.Where(TenantIngestion.AcceptsWebhooks).Any(t => t.Id == e.TenantId));
            flagged = flagged.Where(c => _db.Tenants.Any(t => t.Id == c.TenantId && t.Status == TenantStatus.Active));
        }

        var pending = await events.GroupBy(_ => 1).Select(g => new { Count = g.Count(), Oldest = g.Min(e => (DateTime?)e.ReceivedAt) }).FirstOrDefaultAsync(cancellationToken);
        var maxFailures = _classification.MaxFailures;
        var classification = await flagged.GroupBy(_ => 1)
            .Select(g => new
            {
                Waiting = g.Count(c => c.ClassificationFailures < maxFailures),
                Oldest = g.Where(c => c.ClassificationFailures < maxFailures).Min(c => c.ClassificationRequestedAt),
                Stopped = g.Count(c => c.ClassificationFailures >= maxFailures),
            })
            .FirstOrDefaultAsync(cancellationToken);
        // A count only: which company is in the audit trail and the API log, never here.
        var stuck = 0;
        if (tenantId is null)
        {
            var dayAgo = DateTime.UtcNow.AddDays(-1);
            stuck = await _db.TenantDeletions.AsNoTracking().CountAsync(d => d.CompletedAt == null && (d.StuckSince != null || d.StartedAt < dayAgo), cancellationToken);
        }

        return new BacklogCounts(pending?.Count ?? 0, pending?.Oldest, classification?.Waiting ?? 0, classification?.Oldest, classification?.Stopped ?? 0, stuck);
    }

    private async Task<DeliveryWindows> DeliveriesAsync(Guid? tenantId, DateTime now, CancellationToken cancellationToken)
    {
        var since7d = now.AddDays(-7);
        var since24h = now.AddHours(-24);
        var events = _db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().Where(e => e.ReceivedAt >= since7d);
        if (tenantId is { } id)
        {
            events = events.Where(e => e.TenantId == id);
        }

        // Counters only: the payload column is never part of the projection.
        var groups = await events
            .GroupBy(e => new { e.IsSandbox, Recent = e.ReceivedAt >= since24h })
            .Select(g => new
            {
                g.Key.IsSandbox,
                g.Key.Recent,
                Counts = new DeliveryCounts(
                    g.Count(),
                    g.Sum(e => e.StoredMessageCount ?? 0),
                    g.Sum(e => e.DuplicateMessageCount ?? 0),
                    g.Sum(e => e.SkippedMessageCount ?? 0),
                    g.Count(e => e.Status == WebhookEventStatus.Failed)),
            })
            .ToListAsync(cancellationToken);

        DeliveryCounts Of(bool sandbox, bool recentOnly)
            => groups.Where(g => g.IsSandbox == sandbox && (g.Recent || !recentOnly)).Select(g => g.Counts)
                .Aggregate(DeliveryCounts.Zero, (a, b) => new DeliveryCounts(
                    a.Events + b.Events, a.StoredMessages + b.StoredMessages, a.DuplicateMessages + b.DuplicateMessages, a.SkippedMessages + b.SkippedMessages,
                    a.FailedEvents + b.FailedEvents));

        return new DeliveryWindows(Of(false, true), Of(true, true), Of(false, false), Of(true, false));
    }

    // ErrorLogs is an operations table (no tenant filter): its TenantId is a column like any other. Only fingerprints, counts and kinds leave.
    // One statement: the groups, the top ones by count, and the total of the window (SUM over every group, computed before the LIMIT).
    private async Task<(int Total, IReadOnlyList<ErrorGroupCount> Top)> ErrorsAsync(Guid? tenantId, DateTime now, CancellationToken cancellationToken)
    {
        var since = now.AddHours(-24);
        var rows = tenantId is { } id
            ? await _db.Database.SqlQuery<ErrorRow>($"""
                SELECT g."Fingerprint", g."Source", g."Kind", g."Count", g."Last", (SUM(g."Count") OVER ())::int AS "Total"
                FROM (SELECT "Fingerprint", "Source", "Kind", COUNT(*)::int AS "Count", MAX("OccurredAt") AS "Last"
                      FROM "ErrorLogs" WHERE "OccurredAt" >= {since} AND "TenantId" = {id} GROUP BY "Fingerprint", "Source", "Kind") g
                ORDER BY g."Count" DESC, g."Last" DESC
                LIMIT {TopErrors}
                """).ToListAsync(cancellationToken)
            : await _db.Database.SqlQuery<ErrorRow>($"""
                SELECT g."Fingerprint", g."Source", g."Kind", g."Count", g."Last", (SUM(g."Count") OVER ())::int AS "Total"
                FROM (SELECT "Fingerprint", "Source", "Kind", COUNT(*)::int AS "Count", MAX("OccurredAt") AS "Last"
                      FROM "ErrorLogs" WHERE "OccurredAt" >= {since} GROUP BY "Fingerprint", "Source", "Kind") g
                ORDER BY g."Count" DESC, g."Last" DESC
                LIMIT {TopErrors}
                """).ToListAsync(cancellationToken);
        return (rows.FirstOrDefault()?.Total ?? 0,
            rows.Select(r => new ErrorGroupCount(r.Fingerprint.Trim(), ((ErrorSource)r.Source).ToString(), ((ErrorKind)r.Kind).ToString(), r.Count, r.Last)).ToList());
    }

    private sealed class ErrorRow
    {
        public string Fingerprint { get; set; } = string.Empty;

        public int Source { get; set; }

        public int Kind { get; set; }

        public int Count { get; set; }

        public DateTime Last { get; set; }

        public int Total { get; set; }
    }

    private sealed class SilentRow
    {
        public Guid TenantId { get; set; }

        public string Name { get; set; } = string.Empty;

        public int ActiveLines { get; set; }

        public DateTime? LastDeliveryAt { get; set; }

        public int Total { get; set; }
    }

    private sealed class LimitedRow
    {
        public Guid TenantId { get; set; }

        public string Name { get; set; } = string.Empty;

        public int Conversations { get; set; }

        public int Quota { get; set; }

        public decimal EstimatedUsd { get; set; }

        public decimal CeilingUsd { get; set; }

        public int Total { get; set; }
    }

    private async Task<IReadOnlyList<SendOutcomeCount>> SendsAsync(Guid? tenantId, MonitoringRequest request, CancellationToken cancellationToken)
    {
        var since = request.Today.AddDays(-(SendDays - 1));
        var outcomes = _db.MessageSendOutcomes.IgnoreQueryFilters().AsNoTracking().Where(o => o.Day >= since);
        if (tenantId is { } id)
        {
            outcomes = outcomes.Where(o => o.TenantId == id);
        }

        return await outcomes
            .GroupBy(o => new { o.Outcome, o.IsSandbox })
            .Select(g => new SendOutcomeCount(g.Key.Outcome, g.Key.IsSandbox, g.Sum(o => o.Count)))
            .ToListAsync(cancellationToken);
    }
}
