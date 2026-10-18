using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Conversia.Infrastructure.Metrics;

/// <summary>
/// The metrics aggregations (docs/METRICS.md), one SQL statement each: a CTE of the cohort (the tenant's conversations that started in the
/// period and have a customer message, joined with their opportunity, filtered), aggregated with <c>GROUP BY</c>, <c>count(*) FILTER</c> and
/// <c>percentile_cont</c>. The database returns groups, never conversations; nothing is materialized except the result rows.
/// <para>
/// This is raw SQL because EF Core cannot express ordered-set aggregates (percentiles) nor <c>AT TIME ZONE</c> bucketing, and raw SQL is
/// not touched by the entities' tenant query filters: <b>every statement filters by <c>@tenant</c> itself</b> (the tenant of the current
/// request), on each table it reads. The integration tests prove that no number depends on another tenant's data.
/// </para>
/// </summary>
public sealed class MetricsQueries(ApplicationDbContext db, ICurrentTenantService tenant) : IMetricsQueries
{
    private const string Won = nameof(OpportunityStage.Won);
    private const string Lost = nameof(OpportunityStage.Lost);
    private const string InFollowUp = nameof(OpportunityStage.InFollowUp);
    private const string New = nameof(OpportunityStage.New);

    // The filters a request can add, written once for the cohort and for the message volume. A null parameter means "no filter".
    private const string ConversationFilters = """
        AND (@branch IS NULL OR c."BranchId" = @branch)
        AND (@advisor IS NULL OR c."AdvisorId" = @advisor)
        AND (NOT @unassigned OR c."AdvisorId" IS NULL)
        AND (@source IS NULL OR c."Source" = @source)
        """;

    private const string Cohort = $"""
        WITH cohort AS (
            SELECT c."Id", c."BranchId", c."AdvisorId", c."Source", c."Status", c."StartedAt", c."LastMessageAt", c."FirstInboundAt", c."FirstResponseAt",
                   o."Stage", o."LossReason", o."Objection"
            FROM "Conversations" c
            JOIN "Opportunities" o ON o."ConversationId" = c."Id" AND o."TenantId" = @tenant
            WHERE c."TenantId" = @tenant
              AND c."FirstInboundAt" IS NOT NULL
              AND c."StartedAt" >= @fromUtc AND c."StartedAt" < @toUtc
              {ConversationFilters}
        )
        """;

    // ---- Conversion ---------------------------------------------------------------------------------------------------

    private sealed class ConversionRow
    {
        public Guid? GroupId { get; set; }

        public string? GroupName { get; set; }

        public int Total { get; set; }

        public int Won { get; set; }

        public int Lost { get; set; }

        public int InFollowUp { get; set; }

        public int New { get; set; }

        public int Stale { get; set; }
    }

    public async Task<IReadOnlyList<ConversionRaw>> ConversionAsync(MetricsFilter filter, MetricsGroupBy by, DateTime matureBeforeUtc, CancellationToken cancellationToken = default)
    {
        var (keys, join, group) = Grouping(by);
        var sql = $"""
            {Cohort}
            SELECT {keys},
                   count(*)::int AS "Total",
                   (count(*) FILTER (WHERE k."Stage" = '{Won}'))::int AS "Won",
                   (count(*) FILTER (WHERE k."Stage" = '{Lost}'))::int AS "Lost",
                   (count(*) FILTER (WHERE k."Stage" = '{InFollowUp}'))::int AS "InFollowUp",
                   (count(*) FILTER (WHERE k."Stage" = '{New}'))::int AS "New",
                   (count(*) FILTER (WHERE k."Stage" IN ('{InFollowUp}', '{New}') AND k."LastMessageAt" < @matureBefore))::int AS "Stale"
            FROM cohort k
            {join}
            {group}
            """;
        var rows = await db.Database
            .SqlQueryRaw<ConversionRow>(sql, [.. Parameters(filter), Timestamp("matureBefore", matureBeforeUtc)])
            .ToListAsync(cancellationToken);
        return rows.Select(r => new ConversionRaw(r.GroupId, r.GroupName, r.Total, r.Won, r.Lost, r.InFollowUp, r.New, r.Stale)).ToList();
    }

    // ---- First response -----------------------------------------------------------------------------------------------

    private sealed class ResponseRow
    {
        public Guid? GroupId { get; set; }

        public string? GroupName { get; set; }

        public int Conversations { get; set; }

        public int Answered { get; set; }

        public int Unanswered { get; set; }

        public double? AvgSeconds { get; set; }

        public double? MedianSeconds { get; set; }

        public double? P90Seconds { get; set; }

        public int WithinSla { get; set; }

        public int UnansweredOverSla { get; set; }

        public DateTime? OldestUnansweredInboundAt { get; set; }
    }

    public async Task<IReadOnlyList<ResponseRaw>> FirstResponseAsync(MetricsFilter filter, MetricsGroupBy by, TimeSpan sla, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var (keys, join, group) = Grouping(by);
        const string Seconds = """EXTRACT(EPOCH FROM (k."FirstResponseAt" - k."FirstInboundAt"))::float8""";
        var sql = $"""
            {Cohort}
            SELECT {keys},
                   count(*)::int AS "Conversations",
                   count(k."FirstResponseAt")::int AS "Answered",
                   (count(*) FILTER (WHERE k."FirstResponseAt" IS NULL))::int AS "Unanswered",
                   avg({Seconds})::float8 AS "AvgSeconds",
                   (percentile_cont(0.5) WITHIN GROUP (ORDER BY {Seconds}))::float8 AS "MedianSeconds",
                   (percentile_cont(0.9) WITHIN GROUP (ORDER BY {Seconds}))::float8 AS "P90Seconds",
                   (count(*) FILTER (WHERE k."FirstResponseAt" IS NOT NULL AND k."FirstResponseAt" - k."FirstInboundAt" <= @sla))::int AS "WithinSla",
                   (count(*) FILTER (WHERE k."FirstResponseAt" IS NULL AND k."FirstInboundAt" < @slaDeadline))::int AS "UnansweredOverSla",
                   min(k."FirstInboundAt") FILTER (WHERE k."FirstResponseAt" IS NULL) AS "OldestUnansweredInboundAt"
            FROM cohort k
            {join}
            {group}
            """;
        var rows = await db.Database
            .SqlQueryRaw<ResponseRow>(sql, [.. Parameters(filter), new NpgsqlParameter("sla", NpgsqlDbType.Interval) { Value = sla }, Timestamp("slaDeadline", nowUtc - sla)])
            .ToListAsync(cancellationToken);
        return rows.Select(r => new ResponseRaw(
            r.GroupId, r.GroupName, r.Conversations, r.Answered, r.Unanswered, r.AvgSeconds, r.MedianSeconds, r.P90Seconds, r.WithinSla, r.UnansweredOverSla, r.OldestUnansweredInboundAt)).ToList();
    }

    // ---- Loss reasons and objections ------------------------------------------------------------------------------------

    private sealed class LossReasonRow
    {
        public string? Reason { get; set; }

        public int Count { get; set; }
    }

    private sealed class ObjectionRow
    {
        public string Objection { get; set; } = string.Empty;

        public int Count { get; set; }
    }

    public async Task<IReadOnlyList<LossReasonRaw>> LossReasonsAsync(MetricsFilter filter, CancellationToken cancellationToken = default)
    {
        var rows = await db.Database
            .SqlQueryRaw<LossReasonRow>(
                $"""{Cohort} SELECT k."LossReason" AS "Reason", count(*)::int AS "Count" FROM cohort k WHERE k."Stage" = '{Lost}' GROUP BY k."LossReason" """, Parameters(filter))
            .ToListAsync(cancellationToken);
        return rows.Select(r => new LossReasonRaw(r.Reason, r.Count)).ToList();
    }

    public async Task<IReadOnlyList<ObjectionRaw>> ObjectionsAsync(MetricsFilter filter, CancellationToken cancellationToken = default)
    {
        var rows = await db.Database
            .SqlQueryRaw<ObjectionRow>($"""{Cohort} SELECT k."Objection" AS "Objection", count(*)::int AS "Count" FROM cohort k GROUP BY k."Objection" """, Parameters(filter))
            .ToListAsync(cancellationToken);
        return rows.Select(r => new ObjectionRaw(r.Objection, r.Count)).ToList();
    }

    // ---- Volume ------------------------------------------------------------------------------------------------------

    private sealed class DayRow
    {
        public DateOnly Date { get; set; }

        public int Count { get; set; }
    }

    private sealed class DayMessagesRow
    {
        public DateOnly Date { get; set; }

        public int Inbound { get; set; }

        public int Outbound { get; set; }
    }

    private sealed class HourRow
    {
        public int DayOfWeek { get; set; }

        public int Hour { get; set; }

        public int Count { get; set; }
    }

    public async Task<IReadOnlyList<DayRaw>> ConversationsPerDayAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default)
    {
        var rows = await db.Database
            .SqlQueryRaw<DayRow>(
                $"""{Cohort} SELECT (k."StartedAt" AT TIME ZONE @tz)::date AS "Date", count(*)::int AS "Count" FROM cohort k GROUP BY 1""", [.. Parameters(filter), Text("tz", timeZone)])
            .ToListAsync(cancellationToken);
        return rows.Select(r => new DayRaw(r.Date, r.Count)).ToList();
    }

    public async Task<IReadOnlyList<DayMessagesRaw>> MessagesPerDayAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default)
    {
        // Messages sent in the period, in the conversations that match the filters (whenever those conversations started).
        var sql = $"""
            SELECT (m."SentAt" AT TIME ZONE @tz)::date AS "Date",
                   (count(*) FILTER (WHERE m."Direction" = '{nameof(MessageDirection.Inbound)}'))::int AS "Inbound",
                   (count(*) FILTER (WHERE m."Direction" = '{nameof(MessageDirection.Outbound)}'))::int AS "Outbound"
            FROM "Messages" m
            JOIN "Conversations" c ON c."Id" = m."ConversationId" AND c."TenantId" = @tenant
            WHERE m."TenantId" = @tenant
              AND m."SentAt" >= @fromUtc AND m."SentAt" < @toUtc
              {ConversationFilters}
            GROUP BY 1
            """;
        var rows = await db.Database.SqlQueryRaw<DayMessagesRow>(sql, [.. Parameters(filter), Text("tz", timeZone)]).ToListAsync(cancellationToken);
        return rows.Select(r => new DayMessagesRaw(r.Date, r.Inbound, r.Outbound)).ToList();
    }

    public async Task<IReadOnlyList<HourRaw>> ConversationsPerHourOfWeekAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            {Cohort}
            SELECT EXTRACT(ISODOW FROM (k."StartedAt" AT TIME ZONE @tz))::int AS "DayOfWeek",
                   EXTRACT(HOUR FROM (k."StartedAt" AT TIME ZONE @tz))::int AS "Hour",
                   count(*)::int AS "Count"
            FROM cohort k
            GROUP BY 1, 2
            """;
        var rows = await db.Database.SqlQueryRaw<HourRow>(sql, [.. Parameters(filter), Text("tz", timeZone)]).ToListAsync(cancellationToken);
        return rows.Select(r => new HourRaw(r.DayOfWeek, r.Hour, r.Count)).ToList();
    }

    public async Task<IReadOnlyList<HourRaw>> InboundMessagesPerHourOfWeekAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT EXTRACT(ISODOW FROM (m."SentAt" AT TIME ZONE @tz))::int AS "DayOfWeek",
                   EXTRACT(HOUR FROM (m."SentAt" AT TIME ZONE @tz))::int AS "Hour",
                   count(*)::int AS "Count"
            FROM "Messages" m
            JOIN "Conversations" c ON c."Id" = m."ConversationId" AND c."TenantId" = @tenant
            WHERE m."TenantId" = @tenant
              AND m."Direction" = '{nameof(MessageDirection.Inbound)}'
              AND m."SentAt" >= @fromUtc AND m."SentAt" < @toUtc
              {ConversationFilters}
            GROUP BY 1, 2
            """;
        var rows = await db.Database.SqlQueryRaw<HourRow>(sql, [.. Parameters(filter), Text("tz", timeZone)]).ToListAsync(cancellationToken);
        return rows.Select(r => new HourRaw(r.DayOfWeek, r.Hour, r.Count)).ToList();
    }

    // ---- Unanswered ---------------------------------------------------------------------------------------------------

    private sealed class UnansweredRow
    {
        public Guid ConversationId { get; set; }

        public Guid BranchId { get; set; }

        public string? BranchName { get; set; }

        public Guid? AdvisorId { get; set; }

        public string? AdvisorName { get; set; }

        public DateTime FirstInboundAt { get; set; }
    }

    private sealed class CountRow
    {
        public int Value { get; set; }
    }

    public async Task<(int Total, IReadOnlyList<UnansweredRaw> Items)> UnansweredAsync(MetricsFilter filter, DateTime olderThanUtc, int limit, CancellationToken cancellationToken = default)
    {
        const string Waiting = $"""k."FirstResponseAt" IS NULL AND k."Status" = '{nameof(ConversationStatus.Open)}' AND k."FirstInboundAt" < @olderThan""";
        var olderThan = Timestamp("olderThan", olderThanUtc);

        var total = await db.Database
            .SqlQueryRaw<CountRow>($"""{Cohort} SELECT count(*)::int AS "Value" FROM cohort k WHERE {Waiting}""", [.. Parameters(filter), olderThan])
            .SingleAsync(cancellationToken);

        var sql = $"""
            {Cohort}
            SELECT k."Id" AS "ConversationId", k."BranchId", b."Name" AS "BranchName", k."AdvisorId", a."Name" AS "AdvisorName", k."FirstInboundAt"
            FROM cohort k
            LEFT JOIN "Branches" b ON b."Id" = k."BranchId" AND b."TenantId" = @tenant
            LEFT JOIN "Advisors" a ON a."Id" = k."AdvisorId" AND a."TenantId" = @tenant
            WHERE {Waiting}
            ORDER BY k."FirstInboundAt", k."Id"
            LIMIT @limit
            """;
        var rows = await db.Database
            .SqlQueryRaw<UnansweredRow>(sql, [.. Parameters(filter), Timestamp("olderThan", olderThanUtc), new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit }])
            .ToListAsync(cancellationToken);
        return (total.Value, rows.Select(r => new UnansweredRaw(r.ConversationId, r.BranchId, r.BranchName, r.AdvisorId, r.AdvisorName, r.FirstInboundAt)).ToList());
    }

    // ---- Shared pieces --------------------------------------------------------------------------------------------------

    // The columns that name a group, the join that brings its name, and the GROUP BY. Names come from the tenant's own branches/advisors.
    private static (string Keys, string Join, string Group) Grouping(MetricsGroupBy by) => by switch
    {
        MetricsGroupBy.Branch => (
            """k."BranchId" AS "GroupId", b."Name" AS "GroupName" """,
            """LEFT JOIN "Branches" b ON b."Id" = k."BranchId" AND b."TenantId" = @tenant""",
            """GROUP BY k."BranchId", b."Name" """),
        MetricsGroupBy.Advisor => (
            """k."AdvisorId" AS "GroupId", a."Name" AS "GroupName" """,
            """LEFT JOIN "Advisors" a ON a."Id" = k."AdvisorId" AND a."TenantId" = @tenant""",
            """GROUP BY k."AdvisorId", a."Name" """),
        MetricsGroupBy.Source => (
            """NULL::uuid AS "GroupId", k."Source" AS "GroupName" """,
            string.Empty,
            """GROUP BY k."Source" """),
        _ => ("""NULL::uuid AS "GroupId", NULL::text AS "GroupName" """, string.Empty, string.Empty),
    };

    private NpgsqlParameter[] Parameters(MetricsFilter filter)
        =>
        [
            new("tenant", NpgsqlDbType.Uuid) { Value = tenant.TenantId },
            Timestamp("fromUtc", filter.FromUtc),
            Timestamp("toUtc", filter.ToUtc),
            new("branch", NpgsqlDbType.Uuid) { Value = (object?)filter.BranchId ?? DBNull.Value },
            new("advisor", NpgsqlDbType.Uuid) { Value = (object?)filter.AdvisorId ?? DBNull.Value },
            new("unassigned", NpgsqlDbType.Boolean) { Value = filter.Unassigned },
            new("source", NpgsqlDbType.Varchar) { Value = filter.Source is { } source ? source.ToString() : DBNull.Value },
        ];

    private static NpgsqlParameter Timestamp(string name, DateTime utc)
        => new(name, NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(utc, DateTimeKind.Utc) };

    private static NpgsqlParameter Text(string name, string value) => new(name, NpgsqlDbType.Text) { Value = value };
}
