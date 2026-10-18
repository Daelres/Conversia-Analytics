using Conversia.Application.DTOs.Metrics;
using Conversia.Application.Interfaces;
using Conversia.Application.Metrics;
using Conversia.Application.Services;
using Conversia.Application.Time;
using Conversia.Domain.Enums;
using Conversia.Tests.Shared;

namespace Conversia.Application.UnitTests.Metrics;

/// <summary>
/// The arithmetic and the wording of the metrics, against queries that answer with chosen numbers: what the service does with what the
/// database counted (rates, percentages, top lists, zero-filled days, the grid and the alerts with their sample gates).
/// </summary>
public class MetricsServiceTests
{
    // 2026-11-10 12:00 UTC is 07:00 in Bogota: the local today is 10 November.
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantTimeZone Bogota = new("America/Bogota");

    private sealed class FakeQueries : IMetricsQueries
    {
        public List<MetricsFilter> ConversionFilters { get; } = [];

        public Func<MetricsFilter, MetricsGroupBy, IReadOnlyList<ConversionRaw>> Conversion { get; set; } = (_, _) => [new ConversionRaw(null, null, 0, 0, 0, 0, 0, 0)];

        public Func<MetricsGroupBy, IReadOnlyList<ResponseRaw>> FirstResponse { get; set; } = _ => [new ResponseRaw(null, null, 0, 0, 0, null, null, null, 0, 0, null)];

        public IReadOnlyList<LossReasonRaw> LossReasons { get; set; } = [];

        public IReadOnlyList<ObjectionRaw> Objections { get; set; } = [];

        public IReadOnlyList<DayRaw> Days { get; set; } = [];

        public IReadOnlyList<DayMessagesRaw> DayMessages { get; set; } = [];

        public IReadOnlyList<HourRaw> ConversationHours { get; set; } = [];

        public IReadOnlyList<HourRaw> InboundHours { get; set; } = [];

        public (int Total, IReadOnlyList<UnansweredRaw> Items) Unanswered { get; set; } = (0, []);

        public DateTime? UnansweredOlderThan { get; private set; }

        public MetricsFilter? SeenFilter { get; private set; }

        public DateTime? MatureBefore { get; private set; }

        public Task<IReadOnlyList<ConversionRaw>> ConversionAsync(MetricsFilter filter, MetricsGroupBy by, DateTime matureBeforeUtc, CancellationToken cancellationToken = default)
        {
            ConversionFilters.Add(filter);
            SeenFilter = filter;
            MatureBefore = matureBeforeUtc;
            return Task.FromResult(Conversion(filter, by));
        }

        public Task<IReadOnlyList<ResponseRaw>> FirstResponseAsync(MetricsFilter filter, MetricsGroupBy by, TimeSpan sla, DateTime nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(FirstResponse(by));

        public Task<IReadOnlyList<LossReasonRaw>> LossReasonsAsync(MetricsFilter filter, CancellationToken cancellationToken = default) => Task.FromResult(LossReasons);

        public Task<IReadOnlyList<ObjectionRaw>> ObjectionsAsync(MetricsFilter filter, CancellationToken cancellationToken = default) => Task.FromResult(Objections);

        public Task<IReadOnlyList<DayRaw>> ConversationsPerDayAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default) => Task.FromResult(Days);

        public Task<IReadOnlyList<DayMessagesRaw>> MessagesPerDayAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default) => Task.FromResult(DayMessages);

        public Task<IReadOnlyList<HourRaw>> ConversationsPerHourOfWeekAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default) => Task.FromResult(ConversationHours);

        public Task<IReadOnlyList<HourRaw>> InboundMessagesPerHourOfWeekAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default) => Task.FromResult(InboundHours);

        public Task<(int Total, IReadOnlyList<UnansweredRaw> Items)> UnansweredAsync(MetricsFilter filter, DateTime olderThanUtc, int limit, CancellationToken cancellationToken = default)
        {
            UnansweredOlderThan = olderThanUtc;
            return Task.FromResult(Unanswered);
        }
    }

    private readonly FakeQueries _queries = new();

    private MetricsService Sut(MetricsOptions? options = null, string timeZone = "America/Bogota")
        => new(_queries, options ?? MetricsOptions.Default, new TenantTimeZone(timeZone), new FixedTimeProvider(Now));

    private static ConversionRaw Counts(int won, int lost, int followUp, int @new, int stale, Guid? id = null, string? name = null)
        => new(id, name, won + lost + followUp + @new, won, lost, followUp, @new, stale);

    // ---- The period --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutDates_ThePeriodIsTheLast30LocalDays_IncludingToday()
    {
        var response = await Sut().GetConversionAsync(new MetricsQuery());

        Assert.Equal(new MetricsPeriod(new DateOnly(2026, 10, 12), new DateOnly(2026, 11, 11), "America/Bogota", 30), response.Period);
        Assert.Equal(Bogota.StartOfDayUtc(new DateOnly(2026, 10, 12)), _queries.SeenFilter!.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 11, 5, 0, 0, DateTimeKind.Utc), _queries.SeenFilter.ToUtc); // tomorrow's midnight in Bogota
    }

    [Fact]
    public async Task TheLocalDateOfToday_FollowsTheTimeZone_NotUtc()
    {
        // 03:00 UTC on the 11th is still the 10th at 22:00 in Bogota, but already the 11th in Madrid.
        var late = new FixedTimeProvider(new DateTime(2026, 11, 11, 3, 0, 0, DateTimeKind.Utc));
        var inBogota = new MetricsService(_queries, MetricsOptions.Default, Bogota, late);
        var inMadrid = new MetricsService(_queries, MetricsOptions.Default, new TenantTimeZone("Europe/Madrid"), late);

        Assert.Equal(new DateOnly(2026, 11, 11), (await inBogota.GetConversionAsync(new MetricsQuery())).Period.To);
        Assert.Equal(new DateOnly(2026, 11, 12), (await inMadrid.GetConversionAsync(new MetricsQuery())).Period.To);
    }

    [Fact]
    public async Task OnlyTo_Takes30DaysBefore_AndOnlyFrom_30DaysAfter()
    {
        var onlyTo = await Sut().GetConversionAsync(new MetricsQuery { To = new DateOnly(2026, 10, 1) });
        var onlyFrom = await Sut().GetConversionAsync(new MetricsQuery { From = new DateOnly(2026, 10, 1) });

        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)), (onlyTo.Period.From, onlyTo.Period.To));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), (onlyFrom.Period.From, onlyFrom.Period.To));
    }

    [Fact]
    public async Task AGivenRange_IsUsedAsIs_ToExclusive()
    {
        var response = await Sut().GetConversionAsync(new MetricsQuery { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 11, 1) });

        Assert.Equal(31, response.Period.Days);
        Assert.Equal(new DateTime(2026, 10, 1, 5, 0, 0, DateTimeKind.Utc), _queries.SeenFilter!.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc), _queries.SeenFilter.ToUtc);
    }

    [Fact]
    public async Task TheFiltersTravelToTheQueries()
    {
        var branch = Guid.NewGuid();
        var advisor = Guid.NewGuid();

        await Sut().GetConversionAsync(new MetricsQuery { BranchId = branch, AdvisorId = advisor, Source = "ads" });
        Assert.Equal((branch, advisor, false, ConversationSource.Ads), (_queries.SeenFilter!.BranchId, _queries.SeenFilter.AdvisorId, _queries.SeenFilter.Unassigned, _queries.SeenFilter.Source));

        await Sut().GetConversionAsync(new MetricsQuery { Unassigned = true });
        Assert.Equal((null, null, true, null), (_queries.SeenFilter!.BranchId, _queries.SeenFilter.AdvisorId, _queries.SeenFilter.Unassigned, _queries.SeenFilter.Source));
    }

    [Fact]
    public async Task TheMaturityCutoff_IsTheMaturityDaysBeforeNow()
    {
        await Sut(MetricsOptions.Default with { ConversionMaturityDays = 10 }).GetConversionAsync(new MetricsQuery());

        Assert.Equal(Now.AddDays(-10), _queries.MatureBefore);
    }

    // ---- Conversion --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheConversionRate_IsWonOverWonLostAndStale_AndTheRawCountsComeAlong()
    {
        _queries.Conversion = (_, _) => [Counts(won: 4, lost: 3, followUp: 2, @new: 1, stale: 2)];

        var totals = (await Sut().GetConversionAsync(new MetricsQuery())).Totals;

        Assert.Equal((10, 4, 3, 2, 1, 2), (totals.Total, totals.Won, totals.Lost, totals.InFollowUp, totals.New, totals.Stale));
        Assert.Equal((1, 9), (totals.InProgress, totals.Decided));
        Assert.Equal(44.4, totals.ConversionRate);
        Assert.Equal(40.0, totals.WinShareOfAll);
    }

    [Fact]
    public async Task WithNothingDecided_TheRateIsNull_NotZero()
    {
        _queries.Conversion = (_, _) => [Counts(won: 0, lost: 0, followUp: 3, @new: 0, stale: 0)];

        var totals = (await Sut().GetConversionAsync(new MetricsQuery())).Totals;

        Assert.Null(totals.ConversionRate);
        Assert.Equal(0.0, totals.WinShareOfAll);
        Assert.Equal(3, totals.InProgress);
    }

    [Fact]
    public async Task AnEmptyCohort_HasZeroCountsAndNullRates()
    {
        var totals = (await Sut().GetConversionAsync(new MetricsQuery())).Totals;

        Assert.Equal((0, 0, 0, 0), (totals.Total, totals.Won, totals.Decided, totals.InProgress));
        Assert.Null(totals.ConversionRate);
        Assert.Null(totals.WinShareOfAll);
    }

    [Fact]
    public async Task TheRate_IsRoundedToOneDecimal_HalfAwayFromZero()
    {
        _queries.Conversion = (_, _) => [Counts(won: 1, lost: 15, followUp: 0, @new: 0, stale: 0)]; // 6.25
        Assert.Equal(6.3, (await Sut().GetConversionAsync(new MetricsQuery())).Totals.ConversionRate);

        _queries.Conversion = (_, _) => [Counts(won: 2, lost: 1, followUp: 0, @new: 0, stale: 0)]; // 66.666...
        Assert.Equal(66.7, (await Sut().GetConversionAsync(new MetricsQuery())).Totals.ConversionRate);
    }

    [Fact]
    public async Task TheBreakdowns_AreOrderedByTotalThenName_AndTheUnassignedAdvisorHasItsOwnBucket()
    {
        var ana = Guid.NewGuid();
        var beto = Guid.NewGuid();
        _queries.Conversion = (_, by) => by switch
        {
            MetricsGroupBy.Advisor => [Counts(1, 0, 0, 0, 0, beto, "Beto"), Counts(0, 0, 0, 3, 3, null, null), Counts(1, 1, 0, 0, 0, ana, "Ana"), Counts(1, 0, 0, 0, 0, Guid.NewGuid(), "Abel")],
            MetricsGroupBy.Source => [Counts(1, 0, 0, 0, 0, null, "Organic"), Counts(1, 1, 0, 0, 0, null, "Ads")],
            _ => [Counts(1, 1, 1, 1, 0)],
        };

        var response = await Sut().GetConversionAsync(new MetricsQuery());

        Assert.Equal(["Sin asesor", "Ana", "Abel", "Beto"], response.ByAdvisor.Select(r => r.Name)); // total 3, 2, then the ties by name
        Assert.Null(response.ByAdvisor[0].Id);
        Assert.Equal(["Ads", "Organic"], response.BySource.Select(r => r.Name));
        Assert.Equal(0.0, response.ByAdvisor[0].ConversionRate);
    }

    // ---- First response ----------------------------------------------------------------------------------------------

    private static ResponseRaw Response(
        int answered, int unanswered, double? avg, double? median, double? p90, int withinSla, int unansweredOverSla, DateTime? oldest = null, Guid? id = null, string? name = null)
        => new(id, name, answered + unanswered, answered, unanswered, avg, median, p90, withinSla, unansweredOverSla, oldest);

    [Fact]
    public async Task FirstResponse_ConvertsSecondsToMinutes_AndCountsTheUnansweredApart()
    {
        _queries.FirstResponse = _ => [Response(answered: 9, unanswered: 1, avg: 1720.0, median: 600.0, p90: 4320.0, withinSla: 7, unansweredOverSla: 1, oldest: Now.AddMinutes(-90))];

        var totals = (await Sut().GetFirstResponseAsync(new MetricsQuery())).Totals;

        Assert.Equal((10, 9, 1), (totals.Conversations, totals.Answered, totals.Unanswered));
        Assert.Equal((28.67, 10.0, 72.0), (totals.AvgMinutes, totals.MedianMinutes, totals.P90Minutes));
        Assert.Equal(90.0, totals.OldestUnansweredMinutes);
    }

    [Fact]
    public async Task TheSlaPercent_CountsAnUnansweredPastTheSlaAsAMiss_AndLeavesOutTheOnesStillInsideIt()
    {
        // 9 answered (7 in time), 1 unanswered past the SLA, 2 unanswered still inside it.
        _queries.FirstResponse = _ => [Response(answered: 9, unanswered: 3, avg: 100.0, median: 100.0, p90: 100.0, withinSla: 7, unansweredOverSla: 1)];

        var totals = (await Sut().GetFirstResponseAsync(new MetricsQuery())).Totals;

        Assert.Equal((30, 7, 10, 1), (totals.SlaMinutes, totals.WithinSla, totals.SlaEligible, totals.UnansweredOverSla));
        Assert.Equal(70.0, totals.AnsweredWithinSlaPercent);
    }

    [Fact]
    public async Task WithoutAnsweredConversations_TheStatisticsAreNull_NotZero()
    {
        _queries.FirstResponse = _ => [Response(answered: 0, unanswered: 2, avg: null, median: null, p90: null, withinSla: 0, unansweredOverSla: 2, oldest: Now.AddHours(-2))];

        var totals = (await Sut().GetFirstResponseAsync(new MetricsQuery())).Totals;

        Assert.Equal((null, null, null), (totals.AvgMinutes, totals.MedianMinutes, totals.P90Minutes));
        Assert.Equal(0.0, totals.AnsweredWithinSlaPercent); // two misses, no hit
        Assert.Equal(120.0, totals.OldestUnansweredMinutes);
    }

    [Fact]
    public async Task AnEmptyCohort_HasNoSlaPercent()
    {
        var totals = (await Sut().GetFirstResponseAsync(new MetricsQuery())).Totals;

        Assert.Equal((0, 0, 0), (totals.Conversations, totals.Answered, totals.SlaEligible));
        Assert.Null(totals.AnsweredWithinSlaPercent);
        Assert.Null(totals.OldestUnansweredMinutes);
    }

    [Fact]
    public async Task TheSla_IsConfigurable()
    {
        _queries.FirstResponse = _ => [Response(1, 0, 60.0, 60.0, 60.0, 1, 0)];

        var totals = (await Sut(MetricsOptions.Default with { FirstResponseSlaMinutes = 5 }).GetFirstResponseAsync(new MetricsQuery())).Totals;

        Assert.Equal(5, totals.SlaMinutes);
    }

    // ---- Loss reasons ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTopLossReasons_AreOrderedByCount_TiesByName_WithTheirShare_AndTheRestAsOther()
    {
        _queries.LossReasons = [new("Timing", 2), new("Price", 5), new("Location", 2), new("NotInterested", 1), new("Competitor", 1), new("Other", 1), new(null, 1)];

        var response = await Sut(MetricsOptions.Default with { TopLossReasons = 3 }).GetLossReasonsAsync(new MetricsQuery());

        Assert.Equal(13, response.TotalLost);
        Assert.Equal([("Price", 5, 38.5), ("Location", 2, 15.4), ("Timing", 2, 15.4)], response.TopReasons.Select(r => (r.Reason, r.Count, r.SharePercent)));
        Assert.Equal(4, response.OtherCount); // 13 lost, 9 of them in the top three
    }

    [Fact]
    public async Task ALossWithoutAReason_CountsAsUnknown()
    {
        _queries.LossReasons = [new(null, 2), new("Unknown", 1), new("Price", 1)];

        var response = await Sut().GetLossReasonsAsync(new MetricsQuery());

        Assert.Equal([("Unknown", 3), ("Price", 1)], response.TopReasons.Select(r => (r.Reason, r.Count)));
        Assert.Equal(4, response.TotalLost);
    }

    [Fact]
    public async Task NoLosses_GiveAnEmptyList()
    {
        var response = await Sut().GetLossReasonsAsync(new MetricsQuery());

        Assert.Equal((0, 0), (response.TotalLost, response.OtherCount));
        Assert.Empty(response.TopReasons);
    }

    [Fact]
    public async Task TheObjections_ListEveryTypeInTheEnumOrder_WithShareOfTheWholeCohort()
    {
        _queries.Objections = [new("Pain", 1), new("None", 5), new("Price", 3), new("Timing", 1)];

        var response = await Sut().GetLossReasonsAsync(new MetricsQuery());

        Assert.Equal(10, response.Conversations);
        Assert.Equal(["None", "Price", "Timing", "Location", "Trust", "Pain", "Other"], response.Objections.Select(o => o.Objection));
        Assert.Equal([50.0, 30.0, 10.0, 0.0, 0.0, 10.0, 0.0], response.Objections.Select(o => o.SharePercent));
    }

    // ---- Volume ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheVolume_IsZeroFilledPerDay_AndTotalsAddUp()
    {
        _queries.Days = [new(new DateOnly(2026, 10, 2), 3), new(new DateOnly(2026, 10, 4), 1)];
        _queries.DayMessages = [new(new DateOnly(2026, 10, 2), 5, 4), new(new DateOnly(2026, 10, 3), 0, 2)];

        var response = await Sut().GetVolumeAsync(new MetricsQuery { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 6) });

        Assert.Equal(5, response.ByDay.Count);
        Assert.Equal(
            [("2026-10-01", 0, 0, 0), ("2026-10-02", 3, 5, 4), ("2026-10-03", 0, 0, 2), ("2026-10-04", 1, 0, 0), ("2026-10-05", 0, 0, 0)],
            response.ByDay.Select(d => (d.Date.ToString("yyyy-MM-dd"), d.Conversations, d.InboundMessages, d.OutboundMessages)));
        Assert.Equal((4, 5, 6), (response.Conversations, response.InboundMessages, response.OutboundMessages));
    }

    [Fact]
    public async Task TheHourGrid_Has168Cells_MondayFirst_WithZerosWhereNothingHappened()
    {
        _queries.ConversationHours = [new(1, 10, 2), new(7, 23, 1)];
        _queries.InboundHours = [new(1, 10, 3), new(3, 0, 4)];

        var grid = (await Sut().GetVolumeAsync(new MetricsQuery())).ByHourOfWeek;

        Assert.Equal(168, grid.Count);
        Assert.Equal((1, 0), (grid[0].DayOfWeek, grid[0].Hour));
        Assert.Equal((7, 23), (grid[^1].DayOfWeek, grid[^1].Hour));
        Assert.Equal((2, 3), (grid.Single(c => c is { DayOfWeek: 1, Hour: 10 }).Conversations, grid.Single(c => c is { DayOfWeek: 1, Hour: 10 }).InboundMessages));
        Assert.Equal((0, 4), (grid.Single(c => c is { DayOfWeek: 3, Hour: 0 }).Conversations, grid.Single(c => c is { DayOfWeek: 3, Hour: 0 }).InboundMessages));
        Assert.Equal(1, grid.Single(c => c is { DayOfWeek: 7, Hour: 23 }).Conversations);
        Assert.Equal(3, grid.Count(c => c.Conversations + c.InboundMessages > 0));
    }

    // ---- Alerts ------------------------------------------------------------------------------------------------------

    private static UnansweredRaw Waiting(DateTime inboundAt, Guid? branch = null) => new(Guid.NewGuid(), branch ?? Guid.NewGuid(), "Centro", null, null, inboundAt);

    [Fact]
    public async Task Unanswered_RaisesAWarning_WithTheSlaDeadlineAskedFromTheQueries()
    {
        _queries.Unanswered = (2, [Waiting(Now.AddMinutes(-100)), Waiting(Now.AddMinutes(-45))]);

        var response = await Sut().GetAlertsAsync(new MetricsQuery());

        var alert = Assert.Single(response.Alerts);
        Assert.Equal((AlertCodes.UnansweredOverSla, AlertSeverities.Warning), (alert.Code, alert.Severity));
        Assert.Equal(Now.AddMinutes(-30), _queries.UnansweredOlderThan);
        Assert.Equal(2, alert.Values["count"]);
        Assert.Equal(100.0, alert.Values["oldestWaitingMinutes"]);
        Assert.Equal("Hay 2 conversaciones sin responder hace más de 30 minutos; la más antigua espera 1 h 40 min.", alert.Message);
        Assert.Equal([100.0, 45.0], response.Unanswered.Items.Select(i => i.WaitingMinutes));
        Assert.Equal(2, response.Unanswered.Total);
    }

    [Fact]
    public async Task Unanswered_IsCritical_WhenTheOldestWaitsMoreThanFourTimesTheSla()
    {
        _queries.Unanswered = (1, [Waiting(Now.AddMinutes(-121))]);
        Assert.Equal(AlertSeverities.Critical, Assert.Single((await Sut().GetAlertsAsync(new MetricsQuery())).Alerts).Severity);

        _queries.Unanswered = (1, [Waiting(Now.AddMinutes(-120))]);
        Assert.Equal(AlertSeverities.Warning, Assert.Single((await Sut().GetAlertsAsync(new MetricsQuery())).Alerts).Severity);
    }

    [Fact]
    public async Task NoUnanswered_NoUnansweredAlert()
    {
        var response = await Sut().GetAlertsAsync(new MetricsQuery());

        Assert.Empty(response.Alerts);
        Assert.Equal(0, response.Unanswered.Total);
    }

    [Theory]
    [InlineData(59, "59 min")]
    [InlineData(60, "1 h 0 min")]
    [InlineData(125, "2 h 5 min")]
    [InlineData(2879, "47 h 59 min")]
    [InlineData(2880, "2 d 0 h")]
    [InlineData(47340, "32 d 21 h")]
    public async Task TheWaitingTime_IsWrittenInTheUnitThatReadsBest(int minutes, string expected)
    {
        _queries.Unanswered = (1, [Waiting(Now.AddMinutes(-minutes))]);

        var message = Assert.Single((await Sut().GetAlertsAsync(new MetricsQuery())).Alerts).Message;

        Assert.EndsWith($"la más antigua espera {expected}.", message);
    }

    [Fact]
    public async Task OneUnanswered_IsWrittenInTheSingular()
    {
        _queries.Unanswered = (1, [Waiting(Now.AddMinutes(-45))]);

        var message = Assert.Single((await Sut().GetAlertsAsync(new MetricsQuery())).Alerts).Message;

        Assert.Equal("Hay 1 conversación sin responder hace más de 30 minutos; la más antigua espera 45 min.", message);
    }

    private void RatesByPeriod(Func<MetricsFilter, MetricsGroupBy, IReadOnlyList<ConversionRaw>> conversion) => _queries.Conversion = conversion;

    // The current period is the one that starts at the requested from; the previous one is the same length right before it.
    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly To = new(2026, 11, 1);

    private static bool IsCurrent(MetricsFilter f) => f.FromUtc == Bogota.StartOfDayUtc(From);

    [Fact]
    public async Task AConversionDrop_RaisesAnAlert_WhenBothPeriodsHaveEnoughSampleAndTheDropReachesTheThreshold()
    {
        RatesByPeriod((f, _) => [IsCurrent(f) ? Counts(won: 5, lost: 20, 0, 0, 0) : Counts(won: 15, lost: 10, 0, 0, 0)]); // 20.0 against 60.0

        var response = await Sut().GetAlertsAsync(new MetricsQuery { From = From, To = To });

        var alert = Assert.Single(response.Alerts, a => a.Code == AlertCodes.ConversionDrop);
        Assert.Equal(AlertSeverities.Critical, alert.Severity); // 40 points, at least twice the threshold
        Assert.Equal((20.0, 60.0, 40.0, 25, 25), (alert.Values["currentRate"], alert.Values["previousRate"], alert.Values["dropPoints"], alert.Values["currentSample"], alert.Values["previousSample"]));
        Assert.Equal("La conversión bajó 40,0 puntos frente al periodo anterior (de 60,0 % a 20,0 %).", alert.Message);
    }

    [Fact]
    public async Task ThePreviousPeriod_IsTheSameLengthRightBeforeFrom_WithTheSameFilters()
    {
        RatesByPeriod((_, _) => [Counts(1, 1, 0, 0, 0)]);

        await Sut().GetAlertsAsync(new MetricsQuery { From = From, To = To, AdvisorId = Guid.NewGuid(), Source = "Ads" });
        var previous = _queries.ConversionFilters.Single(f => !IsCurrent(f) && f.ToUtc == Bogota.StartOfDayUtc(From));

        Assert.Equal(Bogota.StartOfDayUtc(From.AddDays(-31)), previous.FromUtc); // October has 31 days: the previous period is the 31 days before the 1st
        Assert.Equal(ConversationSource.Ads, previous.Source);
        Assert.NotNull(previous.AdvisorId);
    }

    [Theory]
    [InlineData(20, 20, true)] // exactly the minimum on both sides
    [InlineData(19, 20, false)] // the current period is one short
    [InlineData(20, 19, false)] // the previous period is one short
    [InlineData(500, 500, true)]
    public async Task TheDropAlert_NeedsTheMinimumSampleOnBothSides(int currentDecided, int previousDecided, bool raised)
    {
        // A collapse from 100 % to 0 %: only the sample decides.
        RatesByPeriod((f, _) => [IsCurrent(f) ? Counts(won: 0, lost: currentDecided, 0, 0, 0) : Counts(won: previousDecided, lost: 0, 0, 0, 0)]);

        var response = await Sut().GetAlertsAsync(new MetricsQuery { From = From, To = To });

        Assert.Equal(raised, response.Alerts.Any(a => a.Code == AlertCodes.ConversionDrop));
    }

    [Theory]
    [InlineData(9, false)] // 9.0 points: just under the 10 threshold
    [InlineData(10, true)] // exactly 10.0
    [InlineData(19, true)] // between the threshold and twice the threshold
    public async Task TheDropAlert_FiresFromTheThreshold_Inclusive(int dropPoints, bool raised)
    {
        // Previous 100 decided at 60.0 %; current 100 decided at (60 - drop) %.
        RatesByPeriod((f, _) => [IsCurrent(f) ? Counts(won: 60 - dropPoints, lost: 40 + dropPoints, 0, 0, 0) : Counts(won: 60, lost: 40, 0, 0, 0)]);

        var response = await Sut().GetAlertsAsync(new MetricsQuery { From = From, To = To });

        Assert.Equal(raised, response.Alerts.Any(a => a.Code == AlertCodes.ConversionDrop));
        if (raised)
        {
            Assert.Equal(AlertSeverities.Warning, response.Alerts.Single(a => a.Code == AlertCodes.ConversionDrop).Severity);
        }
    }

    [Fact]
    public async Task AConversionRise_RaisesNothing()
    {
        RatesByPeriod((f, _) => [IsCurrent(f) ? Counts(won: 60, lost: 40, 0, 0, 0) : Counts(won: 20, lost: 80, 0, 0, 0)]);

        Assert.DoesNotContain(( await Sut().GetAlertsAsync(new MetricsQuery { From = From, To = To })).Alerts, a => a.Code == AlertCodes.ConversionDrop);
    }

    [Fact]
    public async Task ABranchFarBelowTheAverage_RaisesAnAlert_OnlyWithEnoughSampleForTheBranchAndTheTenant()
    {
        var weak = Guid.NewGuid();
        var strong = Guid.NewGuid();
        var small = Guid.NewGuid();
        RatesByPeriod((f, by) => by switch
        {
            // weak: 2 of 20 = 10.0 %; strong: 14 of 20 = 70.0 %; small: 0 of 19 (below the minimum sample, so ignored)
            MetricsGroupBy.Branch when IsCurrent(f) => [Counts(2, 18, 0, 0, 0, weak, "Norte"), Counts(14, 6, 0, 0, 0, strong, "Centro"), Counts(0, 19, 0, 0, 0, small, "Sur")],
            _ => [Counts(16, 43, 0, 0, 0)], // the tenant, in both periods: 16 of 59 = 27.1 %, so there is no drop either
        });

        var response = await Sut().GetAlertsAsync(new MetricsQuery { From = From, To = To });

        var alert = Assert.Single(response.Alerts, a => a.Code == AlertCodes.BranchBelowAverage);
        Assert.Equal(weak, alert.Values["branchId"]);
        Assert.Equal(10.0, alert.Values["branchRate"]);
        Assert.Equal(27.1, alert.Values["tenantRate"]);
        Assert.Equal(17.1, alert.Values["gapPoints"]);
        Assert.Equal(AlertSeverities.Warning, alert.Severity);
        Assert.Equal("La sucursal «Norte» convierte 10,0 % frente al 27,1 % del total: 17,1 puntos por debajo.", alert.Message);
    }

    [Fact]
    public async Task TheBranchAlert_IsNotRaisedWhenTheTenantHasTooFewDecidedConversations()
    {
        RatesByPeriod((_, by) => by == MetricsGroupBy.Branch ? [Counts(0, 20, 0, 0, 0, Guid.NewGuid(), "Norte")] : [Counts(0, 19, 0, 0, 0)]);

        Assert.DoesNotContain((await Sut().GetAlertsAsync(new MetricsQuery { From = From, To = To })).Alerts, a => a.Code == AlertCodes.BranchBelowAverage);
    }

    [Fact]
    public async Task TheBranchAlert_IsNotRaisedWhenAlreadyLookingAtOneBranch()
    {
        RatesByPeriod((_, by) => by == MetricsGroupBy.Branch ? [Counts(0, 20, 0, 0, 0, Guid.NewGuid(), "Norte"), Counts(20, 0, 0, 0, 0, Guid.NewGuid(), "Centro")] : [Counts(20, 20, 0, 0, 0)]);

        var withFilter = await Sut().GetAlertsAsync(new MetricsQuery { From = From, To = To, BranchId = Guid.NewGuid() });
        var without = await Sut().GetAlertsAsync(new MetricsQuery { From = From, To = To });

        Assert.DoesNotContain(withFilter.Alerts, a => a.Code == AlertCodes.BranchBelowAverage);
        Assert.Contains(without.Alerts, a => a.Code == AlertCodes.BranchBelowAverage);
        Assert.Equal(AlertSeverities.Critical, without.Alerts.Single(a => a.Code == AlertCodes.BranchBelowAverage).Severity); // 50 points below
    }

    [Fact]
    public async Task TheSampleSizeAndThresholds_AreConfigurable()
    {
        RatesByPeriod((f, _) => [IsCurrent(f) ? Counts(won: 1, lost: 4, 0, 0, 0) : Counts(won: 3, lost: 2, 0, 0, 0)]); // 20.0 against 60.0 on 5 decided each

        var strict = await Sut().GetAlertsAsync(new MetricsQuery { From = From, To = To });
        var lenient = await Sut(MetricsOptions.Default with { MinSampleSize = 5, ConversionDropAlertPoints = 41 }).GetAlertsAsync(new MetricsQuery { From = From, To = To });
        var sensitive = await Sut(MetricsOptions.Default with { MinSampleSize = 5 }).GetAlertsAsync(new MetricsQuery { From = From, To = To });

        Assert.Empty(strict.Alerts);
        Assert.Empty(lenient.Alerts);
        Assert.Contains(sensitive.Alerts, a => a.Code == AlertCodes.ConversionDrop);
    }

    [Fact]
    public async Task CriticalAlerts_ComeFirst()
    {
        _queries.Unanswered = (1, [Waiting(Now.AddMinutes(-40))]); // a warning
        RatesByPeriod((f, _) => [IsCurrent(f) ? Counts(won: 10, lost: 90, 0, 0, 0) : Counts(won: 60, lost: 40, 0, 0, 0)]); // a critical drop

        var alerts = (await Sut().GetAlertsAsync(new MetricsQuery { From = From, To = To })).Alerts;

        Assert.Equal([AlertCodes.ConversionDrop, AlertCodes.UnansweredOverSla], alerts.Select(a => a.Code));
    }

    // ---- The summary -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSummary_GathersTheHeadlineNumbers()
    {
        _queries.Conversion = (_, _) => [Counts(1, 1, 0, 0, 0)];
        _queries.FirstResponse = _ => [Response(2, 0, 300.0, 300.0, 300.0, 2, 0)];
        _queries.LossReasons = [new("Price", 1)];

        var summary = await Sut().GetSummaryAsync(new MetricsQuery());

        Assert.Equal(50.0, summary.Conversion.ConversionRate);
        Assert.Equal(5.0, summary.FirstResponse.AvgMinutes);
        Assert.Equal((1, "Price"), (summary.TotalLost, summary.TopLossReasons[0].Reason));
    }
}
