using System.Reflection;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Metrics;
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
public sealed class MetricsQueries(ApplicationDbContext db, ICurrentTenantService tenant, MetricsOptions options) : IMetricsQueries
{
    private const string Won = nameof(OpportunityStage.Won);
    private const string Lost = nameof(OpportunityStage.Lost);
    private const string InFollowUp = nameof(OpportunityStage.InFollowUp);
    private const string New = nameof(OpportunityStage.New);
    private const string Open = nameof(ConversationStatus.Open);

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

        public int Matured { get; set; }

        public int MaturedWon { get; set; }

        public int MaturedLost { get; set; }
    }

    // A conversation is matured when it started at or before the cutoff; the rate is computed over those only. "Stale" (undecided and gone cold:
    // its last message is older than the cutoff) is always matured, since a conversation never starts after its last message.
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
                   (count(*) FILTER (WHERE k."Stage" IN ('{InFollowUp}', '{New}') AND k."LastMessageAt" < @matureBefore))::int AS "Stale",
                   (count(*) FILTER (WHERE k."StartedAt" <= @matureBefore))::int AS "Matured",
                   (count(*) FILTER (WHERE k."StartedAt" <= @matureBefore AND k."Stage" = '{Won}'))::int AS "MaturedWon",
                   (count(*) FILTER (WHERE k."StartedAt" <= @matureBefore AND k."Stage" = '{Lost}'))::int AS "MaturedLost"
            FROM cohort k
            {join}
            {group}
            """;
        var rows = await RunAsync<ConversionRow>(sql, [.. Parameters(filter), Timestamp("matureBefore", matureBeforeUtc)], cancellationToken);
        return rows.Select(r => new ConversionRaw(r.GroupId, r.GroupName, r.Total, r.Won, r.Lost, r.InFollowUp, r.New, r.Stale, r.Matured, r.MaturedWon, r.MaturedLost)).ToList();
    }

    // ---- First response -----------------------------------------------------------------------------------------------

    private sealed class ResponseRow
    {
        public Guid? GroupId { get; set; }

        public string? GroupName { get; set; }

        public int Conversations { get; set; }

        public int Answered { get; set; }

        public int UnansweredOpen { get; set; }

        public int UnansweredClosed { get; set; }

        public double? AvgSeconds { get; set; }

        public double? MedianSeconds { get; set; }

        public double? P90Seconds { get; set; }

        public int WithinSla { get; set; }

        public int UnansweredOpenOverSla { get; set; }

        public DateTime? OldestUnansweredOpenInboundAt { get; set; }
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
                   (count(*) FILTER (WHERE k."FirstResponseAt" IS NULL AND k."Status" = '{Open}'))::int AS "UnansweredOpen",
                   (count(*) FILTER (WHERE k."FirstResponseAt" IS NULL AND k."Status" <> '{Open}'))::int AS "UnansweredClosed",
                   avg({Seconds})::float8 AS "AvgSeconds",
                   (percentile_cont(0.5) WITHIN GROUP (ORDER BY {Seconds}))::float8 AS "MedianSeconds",
                   (percentile_cont(0.9) WITHIN GROUP (ORDER BY {Seconds}))::float8 AS "P90Seconds",
                   (count(*) FILTER (WHERE k."FirstResponseAt" IS NOT NULL AND k."FirstResponseAt" - k."FirstInboundAt" <= @sla))::int AS "WithinSla",
                   (count(*) FILTER (WHERE k."FirstResponseAt" IS NULL AND k."Status" = '{Open}' AND k."FirstInboundAt" < @slaDeadline))::int AS "UnansweredOpenOverSla",
                   min(k."FirstInboundAt") FILTER (WHERE k."FirstResponseAt" IS NULL AND k."Status" = '{Open}') AS "OldestUnansweredOpenInboundAt"
            FROM cohort k
            {join}
            {group}
            """;
        var rows = await RunAsync<ResponseRow>(sql, [.. Parameters(filter), new NpgsqlParameter("sla", NpgsqlDbType.Interval) { Value = sla }, Timestamp("slaDeadline", nowUtc - sla)], cancellationToken);
        return rows.Select(r => new ResponseRaw(
            r.GroupId, r.GroupName, r.Conversations, r.Answered, r.UnansweredOpen, r.UnansweredClosed, r.AvgSeconds, r.MedianSeconds, r.P90Seconds, r.WithinSla,
            r.UnansweredOpenOverSla, r.OldestUnansweredOpenInboundAt)).ToList();
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
        var rows = await RunAsync<LossReasonRow>(
            $"""{Cohort} SELECT k."LossReason" AS "Reason", count(*)::int AS "Count" FROM cohort k WHERE k."Stage" = '{Lost}' GROUP BY k."LossReason" """, Parameters(filter), cancellationToken);
        return rows.Select(r => new LossReasonRaw(r.Reason, r.Count)).ToList();
    }

    public async Task<IReadOnlyList<ObjectionRaw>> ObjectionsAsync(MetricsFilter filter, CancellationToken cancellationToken = default)
    {
        var rows = await RunAsync<ObjectionRow>($"""{Cohort} SELECT k."Objection" AS "Objection", count(*)::int AS "Count" FROM cohort k GROUP BY k."Objection" """, Parameters(filter), cancellationToken);
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
        var rows = await RunAsync<DayRow>(
            $"""{Cohort} SELECT (k."StartedAt" AT TIME ZONE @tz)::date AS "Date", count(*)::int AS "Count" FROM cohort k GROUP BY 1""", [.. Parameters(filter), Text("tz", timeZone)], cancellationToken);
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
        var rows = await RunAsync<DayMessagesRow>(sql, [.. Parameters(filter), Text("tz", timeZone)], cancellationToken);
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
        var rows = await RunAsync<HourRow>(sql, [.. Parameters(filter), Text("tz", timeZone)], cancellationToken);
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
        var rows = await RunAsync<HourRow>(sql, [.. Parameters(filter), Text("tz", timeZone)], cancellationToken);
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

        var total = (await RunAsync<CountRow>($"""{Cohort} SELECT count(*)::int AS "Value" FROM cohort k WHERE {Waiting}""", [.. Parameters(filter), olderThan], cancellationToken)).Single();

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
        var rows = await RunAsync<UnansweredRow>(sql, [.. Parameters(filter), Timestamp("olderThan", olderThanUtc), new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit }], cancellationToken);
        return (total.Value, rows.Select(r => new UnansweredRaw(r.ConversationId, r.BranchId, r.BranchName, r.AdvisorId, r.AdvisorName, r.FirstInboundAt)).ToList());
    }

    // ---- Shared pieces --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one statement with the command timeout of <c>Metrics:QueryTimeoutSeconds</c> and reads its rows into <typeparamref name="T"/> by column name.
    /// When the limit passes, the driver asks the server to cancel the statement and this throws <see cref="MetricsQueryTimeoutException"/>, which carries
    /// nothing of the statement. It goes through the connection of the context directly and not through <c>SqlQueryRaw</c> on purpose: EF Core logs a failed
    /// command at error level with its SQL text and the whole exception, and a statement that merely ran out of time is not worth that in a log. (A caller
    /// that went away, through its token, is a plain cancellation.)
    /// </summary>
    private async Task<List<T>> RunAsync<T>(string sql, IEnumerable<NpgsqlParameter> parameters, CancellationToken cancellationToken)
        where T : new()
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = options.QueryTimeoutSeconds };
            command.Parameters.AddRange(parameters.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var columns = new PropertyInfo?[reader.FieldCount];
            for (var i = 0; i < columns.Length; i++)
            {
                columns[i] = typeof(T).GetProperty(reader.GetName(i), BindingFlags.Public | BindingFlags.Instance);
            }

            var rows = new List<T>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new T();
                for (var i = 0; i < columns.Length; i++)
                {
                    if (columns[i] is { } property && !await reader.IsDBNullAsync(i, cancellationToken))
                    {
                        property.SetValue(row, ReadValue(reader, i, Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType));
                    }
                }

                rows.Add(row);
            }

            return rows;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTimeout(ex))
        {
            throw new MetricsQueryTimeoutException();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static object ReadValue(NpgsqlDataReader reader, int ordinal, Type type)
        => type == typeof(Guid) ? reader.GetGuid(ordinal)
            : type == typeof(string) ? reader.GetString(ordinal)
            : type == typeof(int) ? reader.GetInt32(ordinal)
            : type == typeof(double) ? reader.GetDouble(ordinal)
            : type == typeof(bool) ? reader.GetBoolean(ordinal)
            : type == typeof(DateOnly) ? reader.GetFieldValue<DateOnly>(ordinal)
            : type == typeof(DateTime) ? DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc)
            : throw new NotSupportedException($"The metrics rows have no reader for {type.Name}.");

    // The server's answer to a cancelled statement is "query_canceled" (57014); the driver reports its own expired timeout as a TimeoutException, which
    // EF Core wraps (an NpgsqlException inside an InvalidOperationException that suggests enabling retries): look through the causes.
    private static bool IsTimeout(Exception exception)
    {
        for (var cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is TimeoutException or PostgresException { SqlState: PostgresErrorCodes.QueryCanceled })
            {
                return true;
            }
        }

        return false;
    }

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
