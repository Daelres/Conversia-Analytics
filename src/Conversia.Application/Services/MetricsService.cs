using System.Globalization;
using Conversia.Application.DTOs.Metrics;
using Conversia.Application.Interfaces;
using Conversia.Application.Metrics;
using Conversia.Application.Time;
using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Application.Services;

/// <summary>
/// Turns the aggregations of <see cref="IMetricsQueries"/> into the dashboard's numbers, exactly as docs/METRICS.md defines them: the rates,
/// the percentages, the top lists, the zero-filled days and the 24 x 7 grid, and the alerts with their sample gates. The database does the
/// counting; this class does the arithmetic and the wording.
/// </summary>
public class MetricsService : IMetricsService
{
    public const string UnassignedAdvisorName = "Sin asesor";
    public const string InvalidPeriodMessage = "El periodo indicado no es válido.";

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
        try
        {
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

            // The previous period (alerts) starts one length before: all of it must exist on the calendar.
            _ = from.AddDays(-(to.DayNumber - from.DayNumber));
        }
        catch (ArgumentOutOfRangeException)
        {
            // The query validation refuses dates outside DateWindow before they get here; this is for a caller that did not validate.
            throw new DomainException(InvalidPeriodMessage);
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
        var losses = await TopLossReasonsAsync(scope, cancellationToken);
        return new SummaryResponse(scope.Period, conversion, response, losses.TotalLost, losses.StatedLost, losses.NoResponse, losses.Top);
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
        var losses = await TopLossReasonsAsync(scope, cancellationToken);
        var raw = await _queries.ObjectionsAsync(scope.Filter, cancellationToken);
        var cohort = raw.Sum(o => o.Count);
        var counts = raw.ToDictionary(o => o.Objection, o => o.Count, StringComparer.Ordinal);
        var objections = Enum.GetValues<ObjectionType>()
            .Select(type => new ObjectionShare(type.ToString(), counts.GetValueOrDefault(type.ToString()), Percent(counts.GetValueOrDefault(type.ToString()), cohort)))
            .ToList();
        return new LossReasonsResponse(scope.Period, cohort, losses.TotalLost, losses.StatedLost, losses.NoResponse, losses.Top, losses.Other, objections);
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

        // The conversion of this period against the one just before, of the same length. Both are rates of the matured cohort (the conversations
        // old enough to have settled), so a fresh tail never flatters the period. The drop is computed from the exact rates and rounded only
        // for the text: 9.96 points is not a 10-point drop.
        var current = await ConversionTotalsAsync(scope, cancellationToken);
        var previousScope = scope with { Filter = scope.Filter.ForPeriod(_timeZone.StartOfDayUtc(scope.Period.From.AddDays(-scope.Period.Days)), scope.Filter.FromUtc) };
        var previous = await ConversionTotalsAsync(previousScope, cancellationToken);
        if (ExactRate(current.MaturedWon, current.Decided) is { } currentExact && ExactRate(previous.MaturedWon, previous.Decided) is { } previousExact
            && current.Decided >= _options.MinSampleSize && previous.Decided >= _options.MinSampleSize)
        {
            var drop = previousExact - currentExact;
            if (drop >= _options.ConversionDropAlertPoints)
            {
                var shown = Round(drop);
                alerts.Add(new MetricAlert(
                    AlertCodes.ConversionDrop,
                    drop >= 2 * _options.ConversionDropAlertPoints ? AlertSeverities.Critical : AlertSeverities.Warning,
                    $"La conversión bajó {Number(shown)} puntos frente al periodo anterior (de {Number(Round(previousExact))} % a {Number(Round(currentExact))} %).",
                    new Dictionary<string, object?>
                    {
                        ["currentRate"] = Round(currentExact), ["previousRate"] = Round(previousExact), ["dropPoints"] = shown,
                        ["currentSample"] = current.Decided, ["previousSample"] = previous.Decided,
                    }));
            }
        }

        // Branches far below the tenant's average (only when the caller is not looking at one branch).
        if (scope.Filter.BranchId is null && ExactRate(current.MaturedWon, current.Decided) is { } tenantExact && current.Decided >= _options.MinSampleSize)
        {
            foreach (var branch in (await ConversionRowsAsync(scope.Filter, MetricsGroupBy.Branch, scope, cancellationToken))
                         .Where(b => b.Decided >= _options.MinSampleSize && b.Decided > 0)
                         .Select(b => (Row: b, Gap: tenantExact - ExactRate(b.MaturedWon, b.Decided)!.Value))
                         .Where(b => b.Gap >= _options.BranchBelowAveragePoints)
                         .OrderByDescending(b => b.Gap).ThenBy(b => b.Row.Name, StringComparer.Ordinal))
            {
                var shown = Round(branch.Gap);
                alerts.Add(new MetricAlert(
                    AlertCodes.BranchBelowAverage,
                    branch.Gap >= 2 * _options.BranchBelowAveragePoints ? AlertSeverities.Critical : AlertSeverities.Warning,
                    $"La sucursal «{branch.Row.Name}» convierte {Number(branch.Row.ConversionRate!.Value)} % frente al {Number(Round(tenantExact))} % del total: {Number(shown)} puntos por debajo.",
                    new Dictionary<string, object?>
                    {
                        ["branchId"] = branch.Row.Id, ["branchRate"] = branch.Row.ConversionRate, ["tenantRate"] = Round(tenantExact),
                        ["gapPoints"] = shown, ["branchSample"] = branch.Row.Decided,
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
        return ToCounts(rows.Count == 0 ? new ConversionRaw(null, null, 0, 0, 0, 0, 0, 0, 0, 0, 0) : rows[0], new ConversionCounts());
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
        into.Matured = raw.Matured;
        into.Immature = raw.Total - raw.Matured;
        into.MaturedWon = raw.MaturedWon;
        into.MaturedLost = raw.MaturedLost;
        into.Decided = raw.MaturedWon + raw.MaturedLost + raw.Stale;
        into.InMaturation = raw.Total > 0 && raw.Matured == 0;
        into.ConversionRate = Percent(raw.MaturedWon, into.Decided);
        into.WinShareOfAll = Percent(raw.Won, raw.Total);
        return into;
    }

    // ---- First response ---------------------------------------------------------------------------------------------

    private async Task<FirstResponseStats> FirstResponseTotalsAsync(Scope scope, CancellationToken cancellationToken)
    {
        var rows = await _queries.FirstResponseAsync(scope.Filter, MetricsGroupBy.None, _options.FirstResponseSla, scope.NowUtc, cancellationToken);
        return ToStats(rows.Count == 0 ? new ResponseRaw(null, null, 0, 0, 0, 0, null, null, null, 0, 0, null) : rows[0], new FirstResponseStats(), scope.NowUtc);
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
        into.UnansweredOpen = raw.UnansweredOpen;
        into.UnansweredClosed = raw.UnansweredClosed;
        into.AvgMinutes = raw.AvgSeconds is { } avg ? Minutes(avg) : null;
        into.MedianMinutes = raw.MedianSeconds is { } median ? Minutes(median) : null;
        into.P90Minutes = raw.P90Seconds is { } p90 ? Minutes(p90) : null;
        into.SlaMinutes = _options.FirstResponseSlaMinutes;
        into.WithinSla = raw.WithinSla;
        into.UnansweredOpenOverSla = raw.UnansweredOpenOverSla;
        into.SlaEligible = raw.Answered + raw.UnansweredOpenOverSla + raw.UnansweredClosed;
        into.AnsweredWithinSlaPercent = Percent(raw.WithinSla, into.SlaEligible);
        into.OldestUnansweredOpenMinutes = raw.OldestUnansweredOpenInboundAt is { } oldest ? Minutes((nowUtc - oldest).TotalSeconds) : null;
        return into;
    }

    // ---- Loss reasons -----------------------------------------------------------------------------------------------

    private sealed record LossReasons(int TotalLost, int StatedLost, NoResponseLoss NoResponse, IReadOnlyList<LossReasonShare> Top, int Other);

    // A loss without a reason is "Unknown", so the lists always add up to the number of lost sales. NoResponse is the system's inference
    // from silence, not something the customer said: it is a line of its own and the ranking of reasons is made without it.
    private async Task<LossReasons> TopLossReasonsAsync(Scope scope, CancellationToken cancellationToken)
    {
        var reasons = (await _queries.LossReasonsAsync(scope.Filter, cancellationToken))
            .GroupBy(r => r.Reason ?? nameof(LossReason.Unknown))
            .Select(g => (Reason: g.Key, Count: g.Sum(r => r.Count)))
            .ToList();
        var total = reasons.Sum(r => r.Count);
        var silent = reasons.Where(r => r.Reason == nameof(LossReason.NoResponse)).Sum(r => r.Count);
        var stated = reasons.Where(r => r.Reason != nameof(LossReason.NoResponse))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();
        var statedTotal = total - silent;
        var top = stated.Take(_options.TopLossReasons).Select(r => new LossReasonShare(r.Reason, r.Count, Percent(r.Count, statedTotal))).ToList();
        return new LossReasons(total, statedTotal, new NoResponseLoss(silent, Percent(silent, total)), top, statedTotal - top.Sum(r => r.Count));
    }

    // ---- Small helpers ----------------------------------------------------------------------------------------------

    private static string GroupName(MetricsGroupBy by, Guid? id, string? name)
        => by == MetricsGroupBy.Advisor && id is null ? UnassignedAdvisorName : name ?? string.Empty;

    private static double? Percent(int part, int whole)
        => whole <= 0 ? null : Math.Round(100.0 * part / whole, 1, MidpointRounding.AwayFromZero);

    // The exact rate, for comparisons: decimal, so that 7 out of 20 is 35 and not 35.00000000000001. Null when nothing is decided.
    private static decimal? ExactRate(int part, int whole) => whole <= 0 ? null : 100m * part / whole;

    private static double Round(decimal value) => (double)Math.Round(value, 1, MidpointRounding.AwayFromZero);

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
