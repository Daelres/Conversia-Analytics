using Conversia.Application.DTOs.Metrics;
using Conversia.Application.Metrics;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The metrics against a real PostgreSQL, on a small dataset whose every number is worked out by hand (docs/METRICS.md). The clock is fixed
/// (the dates are absolute): nothing here depends on the day the suite runs. The period is October 2026 in Bogota (UTC-5): the cohort is the
/// conversations started from 2026-10-01 05:00 UTC (inclusive) to 2026-11-01 05:00 UTC (exclusive) with a customer message.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class MetricsQueryTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly MetricsQuery October = new() { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 11, 1) };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MetricsQueryTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static DateTime Utc(int month, int day, int hour = 15, int minute = 0, int second = 0) => new(2026, month, day, hour, minute, second, DateTimeKind.Utc);

    private sealed record Dataset(MetricsTenant Tenant, Guid UnansweredId) : IDisposable
    {
        public void Dispose() => Tenant.Dispose();
    }

    /// <summary>
    /// Ten conversations in the cohort of October (c1-c4, c6-c8, c11, c12, c14) and five outside it: c9 (started in November), c10 (the business
    /// wrote first and the customer never did), c13 (one second before the period), c15 (exactly at its end) and the other tenant's.
    /// </summary>
    private async Task<Dataset> SeedOctoberAsync()
    {
        var t = await MetricsTenant.StartAsync(_factory, _client);
        var s = _factory.Services;
        var ana = t.Ana.Id;
        var beto = t.Beto.Id;

        // Centro (Ana)
        await t.SeedAsync(s, false, Utc(10, 5), [In(0), Out(300)], ConversationSource.Ads, ana, OpportunityStage.Won); // c1: answered in 5 min
        await t.SeedAsync(s, false, Utc(10, 6), [In(0), Out(1200)], ConversationSource.Organic, ana, OpportunityStage.Lost, LossReason.Price, ObjectionType.Price); // c2: 20 min
        await t.SeedAsync(s, false, Utc(10, 7), [In(0), Out(3600)], ConversationSource.Organic, ana, OpportunityStage.InFollowUp, objection: ObjectionType.Price); // c3: 60 min, gone cold
        var c4 = await t.SeedAsync(s, false, Utc(10, 8), [In(0)], ConversationSource.Ads); // c4: never answered, no advisor, still new
        await t.SeedAsync(s, false, Utc(10, 1, 5), [In(0), Out(1800)], ConversationSource.Organic, ana, OpportunityStage.Lost, LossReason.Price, ObjectionType.Price); // c12: exactly at the start, 30 min
        // Norte (Beto)
        await t.SeedAsync(s, true, Utc(10, 10), [In(0), Out(60)], ConversationSource.Organic, beto, OpportunityStage.Won); // c6: 1 min
        await t.SeedAsync(s, true, Utc(10, 11), [In(0), Out(120)], ConversationSource.Ads, beto, OpportunityStage.Won); // c7: 2 min
        await t.SeedAsync(s, true, Utc(10, 12), [In(0), Out(600)], ConversationSource.Organic, beto, OpportunityStage.Lost, LossReason.Timing, ObjectionType.Timing); // c8: 10 min
        await t.SeedAsync(s, true, Utc(10, 31, 20), [In(0), Out(600), In(3 * 86_400)], ConversationSource.Organic, beto, OpportunityStage.InFollowUp, objection: ObjectionType.Pain); // c11: still alive on 3 Nov
        await t.SeedAsync(s, true, Utc(11, 1, 4, 59, 59), [In(0), Out(7200)], ConversationSource.Organic, beto, OpportunityStage.Won); // c14: 23:59:59 local on 31 Oct, 120 min

        // Outside the cohort.
        await t.SeedAsync(s, true, Utc(11, 8), [In(0), Out(60)], ConversationSource.Organic, beto, OpportunityStage.Won); // c9: started in November
        await t.SeedAsync(s, false, Utc(10, 15), [Out(0)], ConversationSource.Organic, ana); // c10: the business wrote first; no customer message
        await t.SeedAsync(s, false, Utc(10, 1, 4, 59, 59), [In(0), Out(60)], ConversationSource.Organic, ana, OpportunityStage.Won); // c13: one second before the period
        await t.SeedAsync(s, true, Utc(11, 1, 5), [In(0), Out(60)], ConversationSource.Organic, beto, OpportunityStage.Won); // c15: exactly at the end

        return new Dataset(t, c4.ConversationId);
    }

    private MetricsHarness For(Dataset d, string timeZone = "America/Bogota", MetricsOptions? options = null) => new(_factory.Services, d.Tenant.TenantId, Now, timeZone, options);

    // ---- Conversion --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Conversion_CountsTheCohortByStage_AndRatesItWithTheMaturityRule()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var response = await h.Service.GetConversionAsync(October);

        var totals = response.Totals;
        Assert.Equal((10, 4, 3, 2, 1), (totals.Total, totals.Won, totals.Lost, totals.InFollowUp, totals.New));
        Assert.Equal((2, 1, 9), (totals.Stale, totals.InProgress, totals.Decided)); // c3 and c4 went cold; c11 is still alive
        Assert.Equal(44.4, totals.ConversionRate); // 4 / 9
        Assert.Equal(40.0, totals.WinShareOfAll); // 4 / 10
        Assert.Equal(new MetricsPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), "America/Bogota", 31), response.Period);
    }

    [Fact]
    public async Task Conversion_ByBranch()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var rows = (await h.Service.GetConversionAsync(October)).ByBranch;

        Assert.Equal(["Centro", "Norte"], rows.Select(r => r.Name));
        Assert.Equal([d.Tenant.Centro.Id, d.Tenant.Second.Id], rows.Select(r => r.Id));
        var centro = rows[0];
        Assert.Equal((5, 1, 2, 1, 1, 2, 0, 5), (centro.Total, centro.Won, centro.Lost, centro.InFollowUp, centro.New, centro.Stale, centro.InProgress, centro.Decided));
        Assert.Equal((20.0, 20.0), (centro.ConversionRate, centro.WinShareOfAll));
        var norte = rows[1];
        Assert.Equal((5, 3, 1, 1, 0, 0, 1, 4), (norte.Total, norte.Won, norte.Lost, norte.InFollowUp, norte.New, norte.Stale, norte.InProgress, norte.Decided));
        Assert.Equal((75.0, 60.0), (norte.ConversionRate, norte.WinShareOfAll));
    }

    [Fact]
    public async Task Conversion_ByAdvisor_HasTheUnassignedBucket()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var rows = (await h.Service.GetConversionAsync(October)).ByAdvisor;

        Assert.Equal(["Beto", "Ana", "Sin asesor"], rows.Select(r => r.Name));
        Assert.Equal([d.Tenant.Beto.Id, d.Tenant.Ana.Id, null], rows.Select(r => r.Id));
        Assert.Equal([(5, 75.0), (4, 25.0), (1, 0.0)], rows.Select(r => (r.Total, r.ConversionRate)));
        var unassigned = rows[2];
        Assert.Equal((1, 0, 0, 0, 1, 1, 1), (unassigned.Total, unassigned.Won, unassigned.Lost, unassigned.InFollowUp, unassigned.New, unassigned.Stale, unassigned.Decided));
    }

    [Fact]
    public async Task Conversion_BySource()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var rows = (await h.Service.GetConversionAsync(October)).BySource;

        Assert.Equal(["Organic", "Ads"], rows.Select(r => r.Name));
        Assert.Equal([(7, 2, 3, 6, 33.3), (3, 2, 0, 3, 66.7)], rows.Select(r => (r.Total, r.Won, r.Lost, r.Decided, r.ConversionRate)));
    }

    [Fact]
    public async Task TheFilters_NarrowTheCohort()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var norte = await h.Service.GetConversionAsync(new MetricsQuery { From = October.From, To = October.To, BranchId = d.Tenant.Second.Id });
        var ana = await h.Service.GetConversionAsync(new MetricsQuery { From = October.From, To = October.To, AdvisorId = d.Tenant.Ana.Id });
        var unassigned = await h.Service.GetConversionAsync(new MetricsQuery { From = October.From, To = October.To, Unassigned = true });
        var ads = await h.Service.GetConversionAsync(new MetricsQuery { From = October.From, To = October.To, Source = "Ads" });
        var adsInNorte = await h.Service.GetConversionAsync(new MetricsQuery { From = October.From, To = October.To, Source = "ads", BranchId = d.Tenant.Second.Id });
        var unknown = await h.Service.GetConversionAsync(new MetricsQuery { From = October.From, To = October.To, Source = "Unknown" });

        Assert.Equal((5, 3, 75.0), (norte.Totals.Total, norte.Totals.Won, norte.Totals.ConversionRate));
        Assert.Equal([d.Tenant.Second.Id], norte.ByBranch.Select(r => r.Id));
        Assert.Equal((4, 25.0), (ana.Totals.Total, ana.Totals.ConversionRate));
        Assert.Equal((1, 1), (unassigned.Totals.Total, unassigned.Totals.New));
        Assert.Equal((3, 66.7), (ads.Totals.Total, ads.Totals.ConversionRate));
        Assert.Equal((1, 1), (adsInNorte.Totals.Total, adsInNorte.Totals.Won)); // c7
        Assert.Equal((0, null), (unknown.Totals.Total, unknown.Totals.ConversionRate));
        Assert.Empty(unknown.ByBranch);
    }

    [Fact]
    public async Task TheBoundaries_AreInclusiveAtTheStartAndExclusiveAtTheEnd_InLocalTime()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        // c12 starts at 2026-10-01 05:00:00 UTC (local midnight): in. c13, one second earlier: out. c14 at 23:59:59 local on the 31st: in. c15 at exactly the end: out.
        var october = await h.Service.GetConversionAsync(October);
        var firstDay = await h.Service.GetConversionAsync(new MetricsQuery { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 2) });
        var lastDay = await h.Service.GetConversionAsync(new MetricsQuery { From = new DateOnly(2026, 10, 31), To = new DateOnly(2026, 11, 1) });
        var firstOfNovember = await h.Service.GetConversionAsync(new MetricsQuery { From = new DateOnly(2026, 11, 1), To = new DateOnly(2026, 11, 2) });

        Assert.Equal(10, october.Totals.Total);
        Assert.Equal((1, 0, 1), (firstDay.Totals.Total, firstDay.Totals.Won, firstDay.Totals.Lost)); // only c12
        Assert.Equal((2, 1, 0), (lastDay.Totals.Total, lastDay.Totals.Won, lastDay.Totals.Lost)); // c11 and c14 (c11 in follow-up, c14 won)
        Assert.Equal((1, 1), (firstOfNovember.Totals.Total, firstOfNovember.Totals.Won)); // c15 only: c14 is the 31st in Bogota
    }

    [Fact]
    public async Task AnEmptyPeriod_HasZeroCounts_NullRates_AndEmptyLists()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);
        var january = new MetricsQuery { From = new DateOnly(2026, 1, 1), To = new DateOnly(2026, 1, 8) };

        var conversion = await h.Service.GetConversionAsync(january);
        var response = await h.Service.GetFirstResponseAsync(january);
        var losses = await h.Service.GetLossReasonsAsync(january);
        var volume = await h.Service.GetVolumeAsync(january);
        var alerts = await h.Service.GetAlertsAsync(january);

        Assert.Equal((0, 0, 0, null, null), (conversion.Totals.Total, conversion.Totals.Won, conversion.Totals.Decided, conversion.Totals.ConversionRate, conversion.Totals.WinShareOfAll));
        Assert.Empty(conversion.ByBranch);
        Assert.Equal((0, 0, 0, null, null, null, null), (response.Totals.Conversations, response.Totals.Answered, response.Totals.UnansweredOpen, response.Totals.AvgMinutes, response.Totals.MedianMinutes, response.Totals.P90Minutes, response.Totals.AnsweredWithinSlaPercent));
        Assert.Empty(response.ByAdvisor);
        Assert.Equal((0, 0), (losses.TotalLost, losses.Conversations));
        Assert.Empty(losses.TopReasons);
        Assert.Equal(7, volume.ByDay.Count);
        Assert.All(volume.ByDay, day => Assert.Equal((0, 0, 0), (day.Conversations, day.InboundMessages, day.OutboundMessages)));
        Assert.Equal(168, volume.ByHourOfWeek.Count);
        Assert.Empty(alerts.Alerts);
    }

    // ---- First response ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task FirstResponse_AverageMedianAndP90_OverTheAnsweredOnly_WithTheUnansweredApart()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var totals = (await h.Service.GetFirstResponseAsync(October)).Totals;

        // Minutes of the nine answered: 1, 2, 5, 10, 10, 20, 30, 60, 120. c4 never got an answer and is in none of the statistics.
        Assert.Equal((10, 9, 1), (totals.Conversations, totals.Answered, totals.UnansweredOpen));
        Assert.Equal(28.67, totals.AvgMinutes); // 258 / 9
        Assert.Equal(10.0, totals.MedianMinutes); // the fifth of nine
        Assert.Equal(72.0, totals.P90Minutes); // position 0.9 * 8 = 7.2: 60 + 0.2 * (120 - 60)
        Assert.Equal((30, 7, 10, 70.0), (totals.SlaMinutes, totals.WithinSla, totals.SlaEligible, totals.AnsweredWithinSlaPercent)); // 30 min is within; c4 is a miss
        Assert.Equal((1, 47_340.0), (totals.UnansweredOpenOverSla, totals.OldestUnansweredOpenMinutes)); // 8 Oct 15:00 -> 10 Nov 12:00 = 32 days 21 hours
    }

    [Fact]
    public async Task FirstResponse_ByBranch()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var rows = (await h.Service.GetFirstResponseAsync(October)).ByBranch;

        Assert.Equal(["Centro", "Norte"], rows.Select(r => r.Name));
        var centro = rows[0]; // 5, 20, 60, 30 minutes answered; c4 not
        Assert.Equal((5, 4, 1), (centro.Conversations, centro.Answered, centro.UnansweredOpen));
        Assert.Equal((28.75, 25.0, 51.0), (centro.AvgMinutes, centro.MedianMinutes, centro.P90Minutes));
        Assert.Equal((3, 5, 60.0, 47_340.0), (centro.WithinSla, centro.SlaEligible, centro.AnsweredWithinSlaPercent, centro.OldestUnansweredOpenMinutes));
        var norte = rows[1]; // 1, 2, 10, 10, 120 minutes
        Assert.Equal((5, 5, 0), (norte.Conversations, norte.Answered, norte.UnansweredOpen));
        Assert.Equal((28.6, 10.0, 76.0), (norte.AvgMinutes, norte.MedianMinutes, norte.P90Minutes));
        Assert.Equal((4, 5, 80.0, null), (norte.WithinSla, norte.SlaEligible, norte.AnsweredWithinSlaPercent, norte.OldestUnansweredOpenMinutes));
    }

    [Fact]
    public async Task FirstResponse_ByAdvisor_AndTheUnassignedBucketIsNeverZeroMinutes()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var rows = (await h.Service.GetFirstResponseAsync(October)).ByAdvisor;

        Assert.Equal(["Beto", "Ana", "Sin asesor"], rows.Select(r => r.Name));
        Assert.Equal((28.75, 25.0, 51.0, 75.0), (rows[1].AvgMinutes, rows[1].MedianMinutes, rows[1].P90Minutes, rows[1].AnsweredWithinSlaPercent)); // Ana: 5, 20, 60, 30
        var unassigned = rows[2];
        Assert.Equal((1, 0, 1), (unassigned.Conversations, unassigned.Answered, unassigned.UnansweredOpen));
        Assert.Equal((null, null, null), (unassigned.AvgMinutes, unassigned.MedianMinutes, unassigned.P90Minutes));
        Assert.Equal((0, 1, 0.0), (unassigned.WithinSla, unassigned.SlaEligible, unassigned.AnsweredWithinSlaPercent));
    }

    [Fact]
    public async Task FirstResponse_BySource()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var rows = (await h.Service.GetFirstResponseAsync(October)).BySource;

        Assert.Equal(["Organic", "Ads"], rows.Select(r => r.Name));
        var organic = rows[0]; // 20, 60, 1, 10, 10, 30, 120 minutes
        Assert.Equal((7, 7, 0), (organic.Conversations, organic.Answered, organic.UnansweredOpen));
        Assert.Equal((35.86, 20.0, 84.0, 71.4), (organic.AvgMinutes, organic.MedianMinutes, organic.P90Minutes, organic.AnsweredWithinSlaPercent));
        var ads = rows[1]; // 5 and 2 minutes, and c4 unanswered
        Assert.Equal((3, 2, 1), (ads.Conversations, ads.Answered, ads.UnansweredOpen));
        Assert.Equal((3.5, 3.5, 4.7, 66.7), (ads.AvgMinutes, ads.MedianMinutes, ads.P90Minutes, ads.AnsweredWithinSlaPercent));
    }

    [Fact]
    public async Task TheSla_IsConfigurable_AndInclusive()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d, options: MetricsOptions.Default with { FirstResponseSlaMinutes = 29 });
        using var exact = For(d, options: MetricsOptions.Default with { FirstResponseSlaMinutes = 30 });

        var tight = (await h.Service.GetFirstResponseAsync(October)).Totals;
        var inclusive = (await exact.Service.GetFirstResponseAsync(October)).Totals;

        Assert.Equal((29, 6, 60.0), (tight.SlaMinutes, tight.WithinSla, tight.AnsweredWithinSlaPercent)); // c12's 30 minutes is now late
        Assert.Equal((7, 70.0), (inclusive.WithinSla, inclusive.AnsweredWithinSlaPercent)); // exactly 30 minutes is on time
    }

    [Fact]
    public async Task AnUnansweredConversationStillInsideTheSla_IsNotAMissYet()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        var recent = Utc(11, 10, 11, 50); // 10 minutes before the clock
        await t.SeedAsync(_factory.Services, false, recent, [In(0)]);
        using var h = new MetricsHarness(_factory.Services, t.TenantId, Now);

        var totals = (await h.Service.GetFirstResponseAsync(new MetricsQuery { From = new DateOnly(2026, 11, 1), To = new DateOnly(2026, 11, 11) })).Totals;

        Assert.Equal((1, 0, 1), (totals.Conversations, totals.Answered, totals.UnansweredOpen));
        Assert.Equal((0, 0, 0), (totals.SlaEligible, totals.UnansweredOpenOverSla, totals.WithinSla));
        Assert.Null(totals.AnsweredWithinSlaPercent); // nobody has had their chance yet
        Assert.Equal(10.0, totals.OldestUnansweredOpenMinutes);
    }

    // ---- Loss reasons and objections ----------------------------------------------------------------------------------

    [Fact]
    public async Task LossReasons_AndObjections()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var response = await h.Service.GetLossReasonsAsync(October);

        Assert.Equal((10, 3, 0), (response.Conversations, response.TotalLost, response.OtherCount));
        Assert.Equal([("Price", 2, 66.7), ("Timing", 1, 33.3)], response.TopReasons.Select(r => (r.Reason, r.Count, r.SharePercent)));
        Assert.Equal(
            [("None", 5, 50.0), ("Price", 3, 30.0), ("Timing", 1, 10.0), ("Location", 0, 0.0), ("Trust", 0, 0.0), ("Pain", 1, 10.0), ("Other", 0, 0.0)],
            response.Objections.Select(o => (o.Objection, o.Count, o.SharePercent)));
    }

    [Fact]
    public async Task TheTopListIsCut_AndTheRestIsReportedAsOther()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        var day = Utc(10, 5);
        var reasons = new[] { LossReason.Price, LossReason.Price, LossReason.Price, LossReason.Timing, LossReason.Timing, LossReason.Location, LossReason.Competitor };
        for (var i = 0; i < reasons.Length; i++)
        {
            await t.SeedAsync(_factory.Services, false, day.AddMinutes(i), [In(0), Out(60)], stage: OpportunityStage.Lost, loss: reasons[i]);
        }

        using var h = new MetricsHarness(_factory.Services, t.TenantId, Now, options: MetricsOptions.Default with { TopLossReasons = 2 });
        var response = await h.Service.GetLossReasonsAsync(October);

        Assert.Equal(7, response.TotalLost);
        Assert.Equal([("Price", 3, 42.9), ("Timing", 2, 28.6)], response.TopReasons.Select(r => (r.Reason, r.Count, r.SharePercent)));
        Assert.Equal(2, response.OtherCount);
    }

    // ---- Volume ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Volume_PerDay_InLocalTime_ZeroFilled()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var volume = await h.Service.GetVolumeAsync(October);

        Assert.Equal(31, volume.ByDay.Count);
        var byDay = volume.ByDay.ToDictionary(x => x.Date.Day);
        // (conversations started, inbound, outbound) per local day; the days not listed are zero.
        var expected = new Dictionary<int, (int, int, int)>
        {
            [1] = (1, 1, 2), // c12 at local midnight; c13's reply lands at 00:00:59 local
            [5] = (1, 1, 1), [6] = (1, 1, 1), [7] = (1, 1, 1), [8] = (1, 1, 0),
            [10] = (1, 1, 1), [11] = (1, 1, 1), [12] = (1, 1, 1),
            [15] = (0, 0, 1), // c10: the business wrote to a customer who never answered
            [31] = (2, 2, 1), // c11 at 15:00 and c14 at 23:59:59 local; c14's reply is 02:00 on 1 Nov, out of the period
        };
        foreach (var (day, entry) in byDay)
        {
            Assert.Equal(expected.GetValueOrDefault(day), (entry.Conversations, entry.InboundMessages, entry.OutboundMessages));
        }

        Assert.Equal((10, 10, 10), (volume.Conversations, volume.InboundMessages, volume.OutboundMessages));
    }

    [Fact]
    public async Task Volume_PerHourOfTheWeek_InLocalTime()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var grid = (await h.Service.GetVolumeAsync(October)).ByHourOfWeek;

        Assert.Equal(168, grid.Count);
        // Monday = 1 ... Sunday = 7. Every cohort conversation has exactly one customer message in October, at its start.
        var busy = new Dictionary<(int, int), int>
        {
            [(1, 10)] = 2, [(2, 10)] = 1, [(3, 10)] = 1, [(4, 10)] = 1, [(6, 10)] = 1, [(7, 10)] = 1, [(6, 15)] = 1, [(4, 0)] = 1, [(6, 23)] = 1,
        };
        foreach (var cell in grid)
        {
            var expected = busy.GetValueOrDefault((cell.DayOfWeek, cell.Hour));
            Assert.Equal((expected, expected), (cell.Conversations, cell.InboundMessages));
        }
    }

    [Fact]
    public async Task ALateNightConversation_BelongsToTheLocalDayItStartedIn()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        // 23:30 on 5 October in Bogota is 04:30 UTC on the 6th.
        await t.SeedAsync(_factory.Services, false, Utc(10, 6, 4, 30), [In(0)]);
        using var h = new MetricsHarness(_factory.Services, t.TenantId, Now);

        var volume = await h.Service.GetVolumeAsync(October);

        Assert.Equal(1, volume.ByDay.Single(x => x.Date == new DateOnly(2026, 10, 5)).Conversations);
        Assert.Equal(0, volume.ByDay.Single(x => x.Date == new DateOnly(2026, 10, 6)).Conversations);
        var cell = volume.ByHourOfWeek.Single(x => x.Conversations == 1);
        Assert.Equal((1, 23), (cell.DayOfWeek, cell.Hour)); // Monday 5 October, 23:00
    }

    [Fact]
    public async Task AZoneWithoutDaylightSaving_AheadOfUtc_MovesTheDayTheOtherWay()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        await t.SeedAsync(_factory.Services, false, Utc(10, 5, 16), [In(0)]); // 16:00 UTC is 01:00 the next day in Tokyo (UTC+9)
        using var h = new MetricsHarness(_factory.Services, t.TenantId, Now, "Asia/Tokyo");

        var volume = await h.Service.GetVolumeAsync(new MetricsQuery { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 10) });

        Assert.Equal("Asia/Tokyo", volume.Period.TimeZone);
        Assert.Equal(1, volume.ByDay.Single(x => x.Date == new DateOnly(2026, 10, 6)).Conversations);
        var cell = volume.ByHourOfWeek.Single(x => x.Conversations == 1);
        Assert.Equal((2, 1), (cell.DayOfWeek, cell.Hour)); // Tuesday 6 October, 01:00
    }

    [Fact]
    public async Task AZoneWithDaylightSaving_HasADayOf25Hours_AndTheRepeatedHourIsCountedInItsLocalDay()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        var s = _factory.Services;
        // New York ends daylight saving on Sunday 2026-11-01 at 02:00 local (06:00 UTC), so that day has 25 hours: 04:00 UTC to 05:00 UTC the next day.
        await t.SeedAsync(s, false, new DateTime(2026, 11, 1, 3, 59, 59, DateTimeKind.Utc), [In(0)]); // 23:59:59 on 31 Oct: out
        await t.SeedAsync(s, false, new DateTime(2026, 11, 1, 4, 30, 0, DateTimeKind.Utc), [In(0)]); // 00:30 EDT: in
        await t.SeedAsync(s, false, new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc), [In(0)]); // 01:30 EDT: in
        await t.SeedAsync(s, false, new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc), [In(0)]); // 01:30 EST, the repeated hour: in
        await t.SeedAsync(s, false, new DateTime(2026, 11, 2, 4, 30, 0, DateTimeKind.Utc), [In(0)]); // 23:30 EST: in
        await t.SeedAsync(s, false, new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc), [In(0)]); // 00:00 on 2 Nov: out
        using var h = new MetricsHarness(s, t.TenantId, Now, "America/New_York");

        var volume = await h.Service.GetVolumeAsync(new MetricsQuery { From = new DateOnly(2026, 11, 1), To = new DateOnly(2026, 11, 2) });

        var day = Assert.Single(volume.ByDay);
        Assert.Equal((new DateOnly(2026, 11, 1), 4), (day.Date, day.Conversations));
        Assert.Equal([(7, 0, 1), (7, 1, 2), (7, 23, 1)], volume.ByHourOfWeek.Where(c => c.Conversations > 0).Select(c => (c.DayOfWeek, c.Hour, c.Conversations)));
    }

    // ---- Alerts ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Alerts_ListTheOpenUnansweredConversationsOldestFirst_AndBuildTheMessage()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var response = await h.Service.GetAlertsAsync(October);

        var alert = Assert.Single(response.Alerts); // too few decided conversations for the rate alerts
        Assert.Equal((AlertCodes.UnansweredOverSla, AlertSeverities.Critical), (alert.Code, alert.Severity));
        Assert.Equal("Hay 1 conversación sin responder hace más de 30 minutos; la más antigua espera 32 d 21 h.", alert.Message);
        Assert.Equal((1, 30, 47_340.0), (alert.Values["count"], alert.Values["slaMinutes"], alert.Values["oldestWaitingMinutes"]));
        Assert.Equal(1, response.Unanswered.Total);
        var item = Assert.Single(response.Unanswered.Items);
        Assert.Equal((d.UnansweredId, d.Tenant.Centro.Id, "Centro", null, null, Utc(10, 8), 47_340.0), (item.ConversationId, item.BranchId, item.BranchName, item.AdvisorId, item.AdvisorName, item.FirstInboundAt, item.WaitingMinutes));
        Assert.Equal(Now, response.GeneratedAt);
    }

    [Fact]
    public async Task Alerts_TheUnansweredList_IsTheTwentyOldest_AndAClosedOrAnsweredOneIsNotInIt()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        var s = _factory.Services;
        var oldest = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            var seeded = await t.SeedAsync(s, i % 2 == 0, Utc(10, 2).AddHours(i), [In(0)], advisorId: i % 2 == 0 ? t.Ana.Id : t.Beto.Id);
            oldest.Add(seeded.ConversationId);
        }

        await t.SeedAsync(s, false, Utc(10, 1, 6), [In(0)], closed: true); // closed: history, not a fire
        await t.SeedAsync(s, false, Utc(10, 1, 7), [In(0), Out(60)]); // answered
        await t.SeedAsync(s, false, Utc(11, 10, 11, 45), [In(0)]); // 15 minutes ago: inside the SLA
        using var h = new MetricsHarness(s, t.TenantId, Now);

        var response = await h.Service.GetAlertsAsync(new MetricsQuery { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 11, 11) });

        Assert.Equal(25, response.Unanswered.Total);
        Assert.Equal(20, response.Unanswered.Items.Count);
        Assert.Equal(oldest.Take(20), response.Unanswered.Items.Select(i => i.ConversationId)); // oldest first
        Assert.Equal(response.Unanswered.Items.OrderByDescending(i => i.WaitingMinutes), response.Unanswered.Items);
        Assert.Equal(["Ana", "Beto"], response.Unanswered.Items.Take(2).Select(i => i.AdvisorName));
        Assert.Equal(25, response.Alerts.Single().Values["count"]);
    }

    [Fact]
    public async Task Alerts_ConversionDropAndBranchBelowAverage_NeedTheMinimumSample()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        var s = _factory.Services;
        // The current period is October (31 days), so the one before it is the 31 days from 31 August: September falls inside it.
        var previous = Utc(9, 10);
        var current = Utc(10, 10);
        await SeedDecidedAsync(t, s, previous, north: false, won: 12, lost: 8); // Centro before: 60.0 %
        await SeedDecidedAsync(t, s, previous, north: true, won: 14, lost: 6); // Norte before: 70.0 %
        await SeedDecidedAsync(t, s, current, north: false, won: 2, lost: 18); // Centro now: 10.0 %
        await SeedDecidedAsync(t, s, current, north: true, won: 14, lost: 6); // Norte now: 70.0 %
        using var h = new MetricsHarness(s, t.TenantId, Now);

        var alerts = (await h.Service.GetAlertsAsync(October)).Alerts;

        // Tenant now: 16 of 40 = 40.0 %; before: 26 of 40 = 65.0 %: a drop of 25 points, more than twice the threshold of 10, so Critical.
        var drop = Assert.Single(alerts, a => a.Code == AlertCodes.ConversionDrop);
        Assert.Equal(AlertSeverities.Critical, drop.Severity);
        Assert.Equal((40.0, 65.0, 25.0, 40, 40), (drop.Values["currentRate"], drop.Values["previousRate"], drop.Values["dropPoints"], drop.Values["currentSample"], drop.Values["previousSample"]));
        // Centro converts 10.0 % against the tenant's 40.0 %: 30 points below, twice the threshold of 15: Critical. Norte is above the average.
        var branch = Assert.Single(alerts, a => a.Code == AlertCodes.BranchBelowAverage);
        Assert.Equal(AlertSeverities.Critical, branch.Severity);
        Assert.Equal((t.Centro.Id, 10.0, 40.0, 30.0, 20), (branch.Values["branchId"], branch.Values["branchRate"], branch.Values["tenantRate"], branch.Values["gapPoints"], branch.Values["branchSample"]));
        Assert.Equal("La sucursal «Centro» convierte 10,0 % frente al 40,0 % del total: 30,0 puntos por debajo.", branch.Message);
        Assert.Equal([AlertCodes.ConversionDrop, AlertCodes.BranchBelowAverage], alerts.Select(a => a.Code).Take(2)); // both critical, in the order the rules run
    }

    [Fact]
    public async Task Alerts_OneConversationShortOfTheSample_RaisesNoRateAlert()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        var s = _factory.Services;
        await SeedDecidedAsync(t, s, Utc(9, 10), north: false, won: 18, lost: 2); // 20 before: 90.0 %
        await SeedDecidedAsync(t, s, Utc(10, 10), north: false, won: 0, lost: 19); // 19 now: 0.0 %, one short
        using var h = new MetricsHarness(s, t.TenantId, Now);

        var alerts = (await h.Service.GetAlertsAsync(October)).Alerts;

        Assert.DoesNotContain(alerts, a => a.Code is AlertCodes.ConversionDrop or AlertCodes.BranchBelowAverage);

        await SeedDecidedAsync(t, s, Utc(10, 11), north: false, won: 0, lost: 1); // the twentieth
        var withTwenty = (await h.Service.GetAlertsAsync(October)).Alerts;

        Assert.Contains(withTwenty, a => a.Code == AlertCodes.ConversionDrop);
    }

    // Conversations that reached an outcome (answered fast: they must not raise the unanswered alert).
    private static async Task SeedDecidedAsync(MetricsTenant t, IServiceProvider services, DateTime day, bool north, int won, int lost)
    {
        for (var i = 0; i < won + lost; i++)
        {
            var stage = i < won ? OpportunityStage.Won : OpportunityStage.Lost;
            await t.SeedAsync(services, north, day.AddMinutes(i), [In(0), Out(60)], advisorId: north ? t.Beto.Id : t.Ana.Id, stage: stage, loss: stage == OpportunityStage.Lost ? LossReason.Price : null);
        }
    }

    // ---- Summary and isolation ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Summary_GathersTheHeadline()
    {
        using var d = await SeedOctoberAsync();
        using var h = For(d);

        var summary = await h.Service.GetSummaryAsync(October);

        Assert.Equal((10, 44.4), (summary.Conversion.Total, summary.Conversion.ConversionRate));
        Assert.Equal((28.67, 10.0, 72.0), (summary.FirstResponse.AvgMinutes, summary.FirstResponse.MedianMinutes, summary.FirstResponse.P90Minutes));
        Assert.Equal((3, "Price", 2), (summary.TotalLost, summary.TopLossReasons[0].Reason, summary.TopLossReasons[0].Count));
    }

    [Fact]
    public async Task NoNumberOfATenant_DependsOnAnotherTenantsData()
    {
        using var a = await SeedOctoberAsync();
        using var before = For(a);
        var conversionBefore = await before.Service.GetConversionAsync(October);
        var responseBefore = await before.Service.GetFirstResponseAsync(October);
        var volumeBefore = await before.Service.GetVolumeAsync(October);
        var alertsBefore = await before.Service.GetAlertsAsync(October);

        // A rival tenant with a lot of data in the same period, same branch names, wildly different outcomes.
        using var b = await MetricsTenant.StartAsync(_factory, _client);
        for (var i = 0; i < 30; i++)
        {
            await b.SeedAsync(_factory.Services, i % 2 == 0, Utc(10, 5).AddHours(i), [In(0), Out(7200)], ConversationSource.Ads, i % 2 == 0 ? b.Ana.Id : b.Beto.Id, OpportunityStage.Won, objection: ObjectionType.Trust);
        }

        await b.SeedAsync(_factory.Services, false, Utc(10, 6), [In(0)]);
        using var after = For(a);

        Assert.Equivalent(conversionBefore, await after.Service.GetConversionAsync(October));
        Assert.Equivalent(responseBefore, await after.Service.GetFirstResponseAsync(October));
        Assert.Equivalent(volumeBefore, await after.Service.GetVolumeAsync(October));
        Assert.Equivalent(alertsBefore.Alerts, (await after.Service.GetAlertsAsync(October)).Alerts);
        Assert.Equal(alertsBefore.Unanswered.Items.Select(i => i.ConversationId), (await after.Service.GetAlertsAsync(October)).Unanswered.Items.Select(i => i.ConversationId));
        // And the rival sees none of tenant A's.
        using var rival = new MetricsHarness(_factory.Services, b.TenantId, Now);
        var rivalConversion = await rival.Service.GetConversionAsync(October);
        Assert.Equal((31, 30), (rivalConversion.Totals.Total, rivalConversion.Totals.Won));
        Assert.DoesNotContain(rivalConversion.ByAdvisor, r => r.Id == a.Tenant.Ana.Id || r.Id == a.Tenant.Beto.Id);
        Assert.DoesNotContain(rivalConversion.ByBranch, r => r.Id == a.Tenant.Centro.Id);
    }

    [Fact]
    public async Task AFilterByAnotherTenantsBranchOrAdvisor_FindsNothing()
    {
        using var a = await SeedOctoberAsync();
        using var b = await MetricsTenant.StartAsync(_factory, _client);
        using var h = new MetricsHarness(_factory.Services, b.TenantId, Now);

        var byBranch = await h.Service.GetConversionAsync(new MetricsQuery { From = October.From, To = October.To, BranchId = a.Tenant.Centro.Id });
        var byAdvisor = await h.Service.GetConversionAsync(new MetricsQuery { From = October.From, To = October.To, AdvisorId = a.Tenant.Ana.Id });

        Assert.Equal((0, 0), (byBranch.Totals.Total, byAdvisor.Totals.Total));
    }
}
