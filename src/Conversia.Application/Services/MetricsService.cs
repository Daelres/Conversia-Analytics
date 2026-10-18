using System.Globalization;
using Conversia.Application.DTOs.Metrics;
using Conversia.Application.Interfaces;
using Conversia.Application.Metrics;
using Conversia.Application.Time;
using Conversia.Domain.Common;
using Conversia.Domain.Enums;

namespace Conversia.Application.Services;

/// <summary>
/// Turns the aggregations of <see cref="IMetricsQueries"/> into the dashboard's numbers, exactly as docs/METRICS.md defines them: the rates,
/// the percentages, the top lists, the zero-filled days and the 24 x 7 grid, and the alerts with their sample gates. The database does the
/// counting; this class does the arithmetic and the wording.
/// </summary>
public class MetricsService : IMetricsService
{
    public const string UnassignedAdvisorName = "Sin asesor";

    private static readonly CultureInfo Spanish = SpanishCulture.Info;

    private readonly IMetricsQueries _queries;
    private readonly MetricsOptions _options;
    private readonly TenantTimeZone _timeZone;
    private readonly TimeProvider _timeProvider;

    public MetricsService(IMetricsQueries queries, MetricsOptions options, TenantTimeZone timeZone, TimeProvider timeProvider)
    {
        _queries = queries;
        _options = options;
        _timeZone = timeZone;
        _timeProvider = timeProvider;
    }

    // ---- Resolving a query ------------------------------------------------------------------------------------------

    private sealed record Scope(MetricsPeriod Period, MetricsFilter Filter, DateTime NowUtc)
    {
        public DateTime MatureBeforeUtc(int maturityDays) => NowUtc.AddDays(-maturityDays);
    }

    private Scope Resolve(MetricsQuery query)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var today = _timeZone.LocalDate(now);
        DateOnly from, to;
        if (query.From is { } f && query.To is { } t)
        {
            (from, to) = (f, t);
        }
        else if (query.To is { } onlyTo)
        {
            (from, to) = (onlyTo.AddDays(-MetricsOptions.DefaultRangeDays), onlyTo);
        }
        else if (query.From is { } onlyFrom)
        {
            (from, to) = (onlyFrom, onlyFrom.AddDays(MetricsOptions.DefaultRangeDays));
        }
        else
        {
            (from, to) = (today.AddDays(1 - MetricsOptions.DefaultRangeDays), today.AddDays(1));
        }

        var period = new MetricsPeriod(from, to, _timeZone.Id, to.DayNumber - from.DayNumber);
        var source = string.IsNullOrWhiteSpace(query.Source) ? (ConversationSource?)null : Enum.Parse<ConversationSource>(query.Source.Trim(), ignoreCase: true);
        var filter = new MetricsFilter(_timeZone.StartOfDayUtc(from), _timeZone.StartOfDayUtc(to), query.BranchId, query.AdvisorId, query.Unassigned == true, source);
        return new Scope(period, filter, now);
    }

    // ---- Endpoints --------------------------------------------------------------------------------------------------

    public async Task<SummaryResponse> GetSummaryAsync(MetricsQuery query, CancellationToken cancellationToken = default)
    {
        var scope = Resolve(query);
        var conversion = await ConversionTotalsAsync(scope, cancellationToken);
        var response = await FirstResponseTotalsAsync(scope, cancellationToken);
        var (totalLost, top, _) = await TopLossReasonsAsync(scope, cancellationToken);
        return new SummaryResponse(scope.Period, conversion, response, totalLost, top);
    }

    public async Task<ConversionResponse> GetConversionAsync(MetricsQuery query, CancellationToken cancellationToken = default)
    {
        var scope = Resolve(query);
        var totals = await ConversionTotalsAsync(scope, cancellationToken);
        return new ConversionResponse(
            scope.Period,
            totals,
            await ConversionRowsAsync(scope.Filter, MetricsGroupBy.Branch, scope, cancellationToken),
            await ConversionRowsAsync(scope.Filter, MetricsGroupBy.Advisor, scope, cancellationToken),
            await ConversionRowsAsync(scope.Filter, MetricsGroupBy.Source, scope, cancellationToken));
    }

    public async Task<FirstResponseResponse> GetFirstResponseAsync(MetricsQuery query, CancellationToken cancellationToken = default)
    {
        var scope = Resolve(query);
        var totals = await FirstResponseTotalsAsync(scope, cancellationToken);
        return new FirstResponseResponse(
            scope.Period,
            totals,
            await FirstResponseRowsAsync(scope, MetricsGroupBy.Branch, cancellationToken),
            await FirstResponseRowsAsync(scope, MetricsGroupBy.Advisor, cancellationToken),
            await FirstResponseRowsAsync(scope, MetricsGroupBy.Source, cancellationToken));
    }

    public async Task<LossReasonsResponse> GetLossReasonsAsync(MetricsQuery query, CancellationToken cancellationToken = default)
    {
        var scope = Resolve(query);
        var (totalLost, top, other) = await TopLossReasonsAsync(scope, cancellationToken);
        var raw = await _queries.ObjectionsAsync(scope.Filter, cancellationToken);
        var cohort = raw.Sum(o => o.Count);
        var counts = raw.ToDictionary(o => o.Objection, o => o.Count, StringComparer.Ordinal);
        var objections = Enum.GetValues<ObjectionType>()
            .Select(type => new ObjectionShare(type.ToString(), counts.GetValueOrDefault(type.ToString()), Percent(counts.GetValueOrDefault(type.ToString()), cohort) ?? 0))
            .ToList();
        return new LossReasonsResponse(scope.Period, cohort, totalLost, top, other, objections);
    }

    public async Task<VolumeResponse> GetVolumeAsync(MetricsQuery query, CancellationToken cancellationToken = default)
    {
        var scope = Resolve(query);
        var perDay = (await _queries.ConversationsPerDayAsync(scope.Filter, _timeZone.Id, cancellationToken)).ToDictionary(d => d.Date, d => d.Count);
        var messages = (await _queries.MessagesPerDayAsync(scope.Filter, _timeZone.Id, cancellationToken)).ToDictionary(d => d.Date);

        var days = new List<DayVolume>(scope.Period.Days);
        for (var date = scope.Period.From; date < scope.Period.To; date = date.AddDays(1))
        {
            messages.TryGetValue(date, out var day);
            days.Add(new DayVolume(date, perDay.GetValueOrDefault(date), day?.Inbound ?? 0, day?.Outbound ?? 0));
        }

        var conversationsByHour = (await _queries.ConversationsPerHourOfWeekAsync(scope.Filter, _timeZone.Id, cancellationToken)).ToDictionary(h => (h.DayOfWeek, h.Hour), h => h.Count);
        var inboundByHour = (await _queries.InboundMessagesPerHourOfWeekAsync(scope.Filter, _timeZone.Id, cancellationToken)).ToDictionary(h => (h.DayOfWeek, h.Hour), h => h.Count);
        var grid = new List<HourVolume>(168);
        for (var dayOfWeek = 1; dayOfWeek <= 7; dayOfWeek++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                grid.Add(new HourVolume(dayOfWeek, hour, conversationsByHour.GetValueOrDefault((dayOfWeek, hour)), inboundByHour.GetValueOrDefault((dayOfWeek, hour))));
            }
        }

        return new VolumeResponse(scope.Period, days.Sum(d => d.Conversations), days.Sum(d => d.InboundMessages), days.Sum(d => d.OutboundMessages), days, grid);
    }

    public async Task<AlertsResponse> GetAlertsAsync(MetricsQuery query, CancellationToken cancellationToken = default)
    {
        var scope = Resolve(query);
        var alerts = new List<MetricAlert>();

        // Unanswered conversations that have waited longer than the SLA.
        var deadline = scope.NowUtc - _options.FirstResponseSla;
        var (unansweredTotal, unansweredRaw) = await _queries.UnansweredAsync(scope.Filter, deadline, MetricsOptions.MaxUnansweredListed, cancellationToken);
        var items = unansweredRaw
            .Select(u => new UnansweredConversation(u.ConversationId, u.BranchId, u.BranchName, u.AdvisorId, u.AdvisorName, u.FirstInboundAt, Minutes((scope.NowUtc - u.FirstInboundAt).TotalSeconds)))
            .ToList();
        if (unansweredTotal > 0 && items.Count > 0)
        {
            var oldest = items.Max(i => i.WaitingMinutes);
            alerts.Add(new MetricAlert(
                AlertCodes.UnansweredOverSla,
                oldest > 4 * _options.FirstResponseSlaMinutes ? AlertSeverities.Critical : AlertSeverities.Warning,
                $"Hay {unansweredTotal} {(unansweredTotal == 1 ? "conversación sin responder" : "conversaciones sin responder")} hace más de {_options.FirstResponseSlaMinutes} minutos; la más antigua espera {Wait(oldest)}.",
                new Dictionary<string, object?> { ["count"] = unansweredTotal, ["slaMinutes"] = _options.FirstResponseSlaMinutes, ["oldestWaitingMinutes"] = oldest }));
        }

        // The conversion of this period against the one just before, of the same length.
        var current = await ConversionTotalsAsync(scope, cancellationToken);
        var previousScope = scope with { Filter = scope.Filter.ForPeriod(_timeZone.StartOfDayUtc(scope.Period.From.AddDays(-scope.Period.Days)), scope.Filter.FromUtc) };
        var previous = await ConversionTotalsAsync(previousScope, cancellationToken);
        if (current.ConversionRate is { } currentRate && previous.ConversionRate is { } previousRate
            && current.Decided >= _options.MinSampleSize && previous.Decided >= _options.MinSampleSize)
        {
            var drop = Math.Round(previousRate - currentRate, 1, MidpointRounding.AwayFromZero);
            if (drop >= _options.ConversionDropAlertPoints)
            {
                alerts.Add(new MetricAlert(
                    AlertCodes.ConversionDrop,
                    drop >= 2 * _options.ConversionDropAlertPoints ? AlertSeverities.Critical : AlertSeverities.Warning,
                    $"La conversión bajó {Number(drop)} puntos frente al periodo anterior (de {Number(previousRate)} % a {Number(currentRate)} %).",
                    new Dictionary<string, object?>
                    {
                        ["currentRate"] = currentRate, ["previousRate"] = previousRate, ["dropPoints"] = drop,
                        ["currentSample"] = current.Decided, ["previousSample"] = previous.Decided,
                    }));
            }
        }

        // Branches far below the tenant's average (only when the caller is not looking at one branch).
        if (scope.Filter.BranchId is null && current.ConversionRate is { } tenantRate && current.Decided >= _options.MinSampleSize)
        {
            foreach (var branch in (await ConversionRowsAsync(scope.Filter, MetricsGroupBy.Branch, scope, cancellationToken))
                         .Where(b => b.ConversionRate is not null && b.Decided >= _options.MinSampleSize)
                         .Select(b => (Row: b, Gap: Math.Round(tenantRate - b.ConversionRate!.Value, 1, MidpointRounding.AwayFromZero)))
                         .Where(b => b.Gap >= _options.BranchBelowAveragePoints)
                         .OrderByDescending(b => b.Gap).ThenBy(b => b.Row.Name, StringComparer.Ordinal))
            {
                alerts.Add(new MetricAlert(
                    AlertCodes.BranchBelowAverage,
                    branch.Gap >= 2 * _options.BranchBelowAveragePoints ? AlertSeverities.Critical : AlertSeverities.Warning,
                    $"La sucursal «{branch.Row.Name}» convierte {Number(branch.Row.ConversionRate!.Value)} % frente al {Number(tenantRate)} % del total: {Number(branch.Gap)} puntos por debajo.",
                    new Dictionary<string, object?>
                    {
                        ["branchId"] = branch.Row.Id, ["branchRate"] = branch.Row.ConversionRate, ["tenantRate"] = tenantRate,
                        ["gapPoints"] = branch.Gap, ["branchSample"] = branch.Row.Decided,
                    }));
            }
        }

        var ordered = alerts.OrderBy(a => a.Severity == AlertSeverities.Critical ? 0 : 1).ToList(); // stable: the order above within a severity
        return new AlertsResponse(scope.Period, scope.NowUtc, ordered, new UnansweredSummary(unansweredTotal, items));
    }

    // ---- Conversion -------------------------------------------------------------------------------------------------

    private async Task<ConversionCounts> ConversionTotalsAsync(Scope scope, CancellationToken cancellationToken)
    {
        var rows = await _queries.ConversionAsync(scope.Filter, MetricsGroupBy.None, scope.MatureBeforeUtc(_options.ConversionMaturityDays), cancellationToken);
        return ToCounts(rows.Count == 0 ? new ConversionRaw(null, null, 0, 0, 0, 0, 0, 0) : rows[0], new ConversionCounts());
    }

    private async Task<IReadOnlyList<ConversionRow>> ConversionRowsAsync(MetricsFilter filter, MetricsGroupBy by, Scope scope, CancellationToken cancellationToken)
    {
        var rows = await _queries.ConversionAsync(filter, by, scope.MatureBeforeUtc(_options.ConversionMaturityDays), cancellationToken);
        return rows
            .Select(r => ToCounts(r, new ConversionRow { Id = r.GroupId, Name = GroupName(by, r.GroupId, r.GroupName) }))
            .OrderByDescending(r => r.Total).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static T ToCounts<T>(ConversionRaw raw, T into)
        where T : ConversionCounts
    {
        into.Total = raw.Total;
        into.Won = raw.Won;
        into.Lost = raw.Lost;
        into.InFollowUp = raw.InFollowUp;
        into.New = raw.New;
        into.Stale = raw.Stale;
        into.InProgress = raw.InFollowUp + raw.New - raw.Stale;
        into.Decided = raw.Won + raw.Lost + raw.Stale;
        into.ConversionRate = Percent(raw.Won, into.Decided);
        into.WinShareOfAll = Percent(raw.Won, raw.Total);
        return into;
    }

    // ---- First response ---------------------------------------------------------------------------------------------

    private async Task<FirstResponseStats> FirstResponseTotalsAsync(Scope scope, CancellationToken cancellationToken)
    {
        var rows = await _queries.FirstResponseAsync(scope.Filter, MetricsGroupBy.None, _options.FirstResponseSla, scope.NowUtc, cancellationToken);
        return ToStats(rows.Count == 0 ? new ResponseRaw(null, null, 0, 0, 0, null, null, null, 0, 0, null) : rows[0], new FirstResponseStats(), scope.NowUtc);
    }

    private async Task<IReadOnlyList<FirstResponseRow>> FirstResponseRowsAsync(Scope scope, MetricsGroupBy by, CancellationToken cancellationToken)
    {
        var rows = await _queries.FirstResponseAsync(scope.Filter, by, _options.FirstResponseSla, scope.NowUtc, cancellationToken);
        return rows
            .Select(r => ToStats(r, new FirstResponseRow { Id = r.GroupId, Name = GroupName(by, r.GroupId, r.GroupName) }, scope.NowUtc))
            .OrderByDescending(r => r.Conversations).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    private T ToStats<T>(ResponseRaw raw, T into, DateTime nowUtc)
        where T : FirstResponseStats
    {
        into.Conversations = raw.Conversations;
        into.Answered = raw.Answered;
        into.Unanswered = raw.Unanswered;
        into.AvgMinutes = raw.AvgSeconds is { } avg ? Minutes(avg) : null;
        into.MedianMinutes = raw.MedianSeconds is { } median ? Minutes(median) : null;
        into.P90Minutes = raw.P90Seconds is { } p90 ? Minutes(p90) : null;
        into.SlaMinutes = _options.FirstResponseSlaMinutes;
        into.WithinSla = raw.WithinSla;
        into.UnansweredOverSla = raw.UnansweredOverSla;
        into.SlaEligible = raw.Answered + raw.UnansweredOverSla;
        into.AnsweredWithinSlaPercent = Percent(raw.WithinSla, into.SlaEligible);
        into.OldestUnansweredMinutes = raw.OldestUnansweredInboundAt is { } oldest ? Minutes((nowUtc - oldest).TotalSeconds) : null;
        return into;
    }

    // ---- Loss reasons -----------------------------------------------------------------------------------------------

    private async Task<(int TotalLost, IReadOnlyList<LossReasonShare> Top, int Other)> TopLossReasonsAsync(Scope scope, CancellationToken cancellationToken)
    {
        // A loss without a reason is "Unknown", so the list always adds up to the number of lost sales.
        var reasons = (await _queries.LossReasonsAsync(scope.Filter, cancellationToken))
            .GroupBy(r => r.Reason ?? nameof(LossReason.Unknown))
            .Select(g => (Reason: g.Key, Count: g.Sum(r => r.Count)))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();
        var total = reasons.Sum(r => r.Count);
        var top = reasons.Take(_options.TopLossReasons).Select(r => new LossReasonShare(r.Reason, r.Count, Percent(r.Count, total) ?? 0)).ToList();
        return (total, top, total - top.Sum(r => r.Count));
    }

    // ---- Small helpers ----------------------------------------------------------------------------------------------

    private static string GroupName(MetricsGroupBy by, Guid? id, string? name)
        => by == MetricsGroupBy.Advisor && id is null ? UnassignedAdvisorName : name ?? string.Empty;

    private static double? Percent(int part, int whole)
        => whole <= 0 ? null : Math.Round(100.0 * part / whole, 1, MidpointRounding.AwayFromZero);

    private static double Minutes(double seconds) => Math.Round(seconds / 60.0, 2, MidpointRounding.AwayFromZero);

    private static string Number(double value) => value.ToString("0.0", Spanish);

    // "45 min", "2 h 5 min", "32 d 21 h": what a manager reads at a glance.
    private static string Wait(double minutes)
    {
        var total = (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
        return total switch
        {
            < 60 => $"{total} min",
            < 48 * 60 => $"{total / 60} h {total % 60} min",
            _ => $"{total / (24 * 60)} d {total % (24 * 60) / 60} h",
        };
    }
}
