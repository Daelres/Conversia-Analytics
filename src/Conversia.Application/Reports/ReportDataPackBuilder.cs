using Conversia.Application.DTOs.Metrics;
using Conversia.Application.Interfaces;
using Conversia.Application.Metrics;
using Conversia.Domain.Enums;

namespace Conversia.Application.Reports;

/// <summary>What a pack is built for: the company, the period, the branch scope and the sections.</summary>
/// <param name="ScopeBranches">The chosen branches; empty = all of them.</param>
public sealed record ReportPackRequest(
    string Company, ReportFrequency Frequency, ReportPeriod Period, IReadOnlyList<(Guid Id, string Name)> ScopeBranches, ReportSections Sections);

/// <summary>
/// Builds the data pack of a report from the existing metrics (docs/METRICS.md) of the current tenant: the same cohort, filters, sandbox rule
/// and definitions as the dashboard, for the report's period and branches. Only aggregates and the company's own names go in; the shares and
/// differences a report may quote are computed here, so a text never needs to compute one.
/// <para>
/// <b>Conversion</b> is measured over the latest window of the period's length whose conversations are all matured (it ends at least
/// <c>Metrics:ConversionMaturityDays</c> before today), against the window right before it; a rate is stated only with at least
/// <c>Reports:MinConversionSample</c> decided conversations, and the change only when both rates are stated. <b>Unanswered</b> conversations are
/// the open ones waiting now, whenever they started (what "responder hoy" needs).
/// </para>
/// </summary>
public sealed class ReportDataPackBuilder(
    IMetricsService metrics, IMetricsQueries queries, IAiAnalysisStatusQueries aiStatus, MetricsOptions options, ReportSettings settings, TimeProvider time)
{
    /// <summary>Advisors listed in the conversion section (the ones with the most conversations).</summary>
    public const int MaxAdvisors = 8;

    public const int MaxBranches = 12;

    public async Task<ReportDataPack> BuildAsync(ReportPackRequest request, TimeZoneInfo zone, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var period = request.Period;
        var sections = request.Sections;
        var branchIds = request.ScopeBranches.Select(b => b.Id).ToList();
        MetricsQuery Query(ReportPeriod p) => new MetricsQuery { From = p.From, To = p.To }.WithBranches(branchIds);

        var now = time.GetUtcNow().UtcDateTime;
        var window = MaturedWindow(request.Frequency, period, ReportSchedule.LocalDate(now, zone), options.ConversionMaturityDays);
        var wantsConversion = (sections & (ReportSections.Headline | ReportSections.Conversion)) != 0;
        var conversion = wantsConversion ? await metrics.GetConversionAsync(Query(window.Current), cancellationToken) : null;
        var previousConversion = sections.HasFlag(ReportSections.Headline) ? await metrics.GetConversionAsync(Query(window.Previous), cancellationToken) : null;
        var volume = sections.HasFlag(ReportSections.Headline) ? await metrics.GetConversionAsync(Query(period), cancellationToken) : null;
        var previousVolume = sections.HasFlag(ReportSections.Headline) ? await metrics.GetConversionAsync(Query(Previous(request.Frequency, period)), cancellationToken) : null;
        var response = sections.HasFlag(ReportSections.Response) ? await metrics.GetFirstResponseAsync(Query(period), cancellationToken) : null;
        var waiting = sections.HasFlag(ReportSections.Response) ? await WaitingNowAsync(branchIds, now, response!.Totals.SlaMinutes, cancellationToken) : default;
        var losses = sections.HasFlag(ReportSections.LossReasons) ? await metrics.GetLossReasonsAsync(Query(period), cancellationToken) : null;
        var closing = (sections & (ReportSections.DropOff | ReportSections.AiCoverage)) != 0 ? await metrics.GetClosingPointAsync(Query(period), cancellationToken) : null;
        var ai = sections.HasFlag(ReportSections.AiCoverage) ? await aiStatus.GetAsync(cancellationToken) : null;

        var scope = Scope(request.ScopeBranches.Select(b => b.Name).Order(StringComparer.Ordinal).ToList());

        return new ReportDataPack(
            ReportDataPack.CurrentVersion,
            request.Company,
            ReportText.Frequency(request.Frequency),
            new ReportPackPeriod(period.From, period.To.AddDays(-1), period.Days, ReportText.PeriodLabel(request.Frequency, period.From, period.To)),
            scope,
            Enum.GetValues<ReportSections>().Where(s => s is not ReportSections.None and not ReportSections.All && sections.HasFlag(s)).Select(s => s.ToString()).ToList(),
            sections.HasFlag(ReportSections.Headline) ? Headline(volume!.Totals, previousVolume!.Totals, Rate(window.Current, conversion!.Totals), Rate(window.Previous, previousConversion!.Totals)) : null,
            sections.HasFlag(ReportSections.Conversion) && conversion is not null ? Conversion(conversion, window.Current, request.ScopeBranches.Count != 1) : null,
            sections.HasFlag(ReportSections.DropOff) && closing is not null ? DropOff(closing) : null,
            losses is null ? null : Losses(losses),
            response is null ? null : Response(response, waiting),
            ai is null || closing is null ? null : Coverage(ai, closing, zone));
    }

    /// <summary>The period of the same frequency right before: the day, the week or the month before.</summary>
    public static ReportPeriod Previous(ReportFrequency frequency, ReportPeriod period) => frequency switch
    {
        ReportFrequency.Monthly => new ReportPeriod(period.From.AddMonths(-1), period.From),
        _ => new ReportPeriod(period.From.AddDays(-period.Days), period.From),
    };

    /// <summary>
    /// The window of the period's length whose conversations have all matured on <paramref name="today"/>: the period itself when it ended at
    /// least the maturity ago, otherwise the period moved back just enough; and the window of the same length right before it.
    /// </summary>
    public static (ReportPeriod Current, ReportPeriod Previous) MaturedWindow(ReportFrequency frequency, ReportPeriod period, DateOnly today, int maturityDays)
    {
        var shift = Math.Max(0, maturityDays - (today.DayNumber - period.To.DayNumber));
        if (shift == 0)
        {
            return (period, Previous(frequency, period));
        }

        var current = new ReportPeriod(period.From.AddDays(-shift), period.To.AddDays(-shift));
        return (current, new ReportPeriod(current.From.AddDays(-current.Days), current.From));
    }

    /// <summary>"Sucursal Norte", "Sucursales: Centro y Norte", "Todas las sucursales"; a name that already says "Sucursal" is not repeated.</summary>
    public static string Scope(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "Todas las sucursales",
        1 when names[0].StartsWith("Sucursal", StringComparison.OrdinalIgnoreCase) => names[0],
        1 => "Sucursal " + names[0],
        _ => "Sucursales: " + ReportText.List(names),
    };

    // A rate is stated only over enough decided conversations, all of them matured.
    private ReportConversionRate Rate(ReportPeriod window, ConversionCounts counts)
        => new(window.From, window.To.AddDays(-1), ReportText.RangeLabel(window.From, window.To), counts.Decided, counts.MaturedWon,
            counts.Decided >= settings.MinConversionSample && counts.Immature == 0 ? counts.ConversionRate : null);

    private ReportHeadline Headline(ConversionCounts volume, ConversionCounts previousVolume, ReportConversionRate current, ReportConversionRate previous)
        => new(volume.Total, volume.Won, previousVolume.Total, options.ConversionMaturityDays, settings.MinConversionSample, current, previous,
            current.Rate is { } c && previous.Rate is { } p ? Math.Round(c - p, 1) : null);

    private ReportConversion Conversion(ConversionResponse conversion, ReportPeriod window, bool byBranch)
    {
        ReportGroupRate Row(ConversionRow r, string name)
            => new(name, r.Total, r.Decided, r.MaturedWon, r.Decided >= settings.MinConversionSample ? r.ConversionRate : null);

        return new ReportConversion(
            window.From, window.To.AddDays(-1), ReportText.RangeLabel(window.From, window.To),
            conversion.Totals.Decided >= settings.MinConversionSample && conversion.Totals.Immature == 0 ? conversion.Totals.ConversionRate : null,
            settings.MinConversionSample,
            byBranch ? conversion.ByBranch.Where(r => r.Total > 0).Take(MaxBranches).Select(r => Row(r, r.Name)).ToList() : [],
            conversion.ByAdvisor.Where(r => r.Total > 0).OrderByDescending(r => r.Total).Take(MaxAdvisors).Select(r => Row(r, r.Name)).ToList(),
            conversion.BySource.Where(r => r.Total > 0).Select(r => Row(r, ReportText.Source(r.Name))).ToList());
    }

    /// <summary>How far back an open conversation can still be waiting (the auto-close keeps them much younger).</summary>
    public const int WaitingLookbackDays = 90;

    // Open conversations waiting for a reply now, whenever they started (within the lookback), of the report's branches.
    private async Task<(int Total, int OverSla, double? OldestMinutes)> WaitingNowAsync(IReadOnlyList<Guid> branchIds, DateTime now, int slaMinutes, CancellationToken cancellationToken)
    {
        var filter = new MetricsFilter(now.AddDays(-WaitingLookbackDays), now.AddDays(1), null, null, false, null, branchIds.Count == 0 ? null : branchIds);
        var (total, oldest) = await queries.UnansweredAsync(filter, now, 1, cancellationToken);
        var (overSla, _) = await queries.UnansweredAsync(filter, now.AddMinutes(-slaMinutes), 1, cancellationToken);
        return (total, overSla, oldest is [var first, ..] ? (now - first.FirstInboundAt).TotalMinutes : null);
    }

    private static ReportDropOff DropOff(ClosingPointResponse closing)
    {
        var main = closing.MainDropOff is { } stage ? closing.Stages.FirstOrDefault(s => s.Stage == stage) : null;
        return new ReportDropOff(closing.Stopped, closing.Stopped - closing.NotRead, main is null ? null : ReportText.ReachedStage(main.Stage), main?.Count,
            main?.SharePercent, closing.MovedOffChannel.Count);
    }

    private static ReportLosses Losses(LossReasonsResponse losses)
        => new(
            losses.TotalLost,
            losses.NoResponse.Count,
            losses.NoResponse.SharePercent,
            losses.TopReasons.Where(r => r.Count > 0).Take(3).Select(r => new ReportShare(ReportText.LossReason(r.Reason), r.Count, r.SharePercent)).ToList(),
            losses.Objections.Where(o => o.Objection != nameof(ObjectionType.None) && o.Count > 0).OrderByDescending(o => o.Count).Take(3)
                .Select(o => new ReportShare(ReportText.Objection(o.Objection), o.Count, o.SharePercent)).ToList());

    private static ReportResponseTimes Response(FirstResponseResponse response, (int Total, int OverSla, double? OldestMinutes) waiting)
    {
        var totals = response.Totals;
        return new ReportResponseTimes(
            totals.SlaMinutes,
            totals.MedianMinutes is { } median ? Math.Round(median, 1) : null,
            totals.AnsweredWithinSlaPercent,
            totals.Answered,
            waiting.Total,
            waiting.OverSla,
            waiting.OldestMinutes is { } minutes ? Math.Round(minutes / 60, 1) : null,
            response.ByBranch.Where(b => b.Answered > 0 && b.MedianMinutes is not null).OrderByDescending(b => b.MedianMinutes).Take(3)
                .Select(b => new ReportGroupResponse(b.Name, Math.Round(b.MedianMinutes!.Value, 1), b.AnsweredWithinSlaPercent, b.Answered)).ToList());
    }

    private static ReportCoverage Coverage(AiAnalysisStatusResponse ai, ClosingPointResponse closing, TimeZoneInfo zone)
    {
        var read = closing.Stopped - closing.NotRead;
        return new ReportCoverage(
            ai.Enabled, closing.Stopped, closing.NotRead, closing.Stopped == 0 ? null : Math.Round(100.0 * read / closing.Stopped, 1),
            ai.LastUpdatedAt is { } last ? ReportSchedule.LocalDate(last, zone) : null);
    }
}
