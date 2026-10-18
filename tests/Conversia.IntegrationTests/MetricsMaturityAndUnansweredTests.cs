using Conversia.Application.DTOs.Metrics;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Two definitions of docs/METRICS.md that the SQL must honor, on a small dataset worked out by hand: the conversion rate is computed over the
/// matured cohort (a fresh tail never flatters it), and "unanswered" is split into open (somebody can still answer) and closed (nobody did).
/// The clock is fixed at 2026-11-10 12:00 UTC, so a conversation is matured when it started at or before 2026-11-03 12:00 UTC.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class MetricsMaturityAndUnansweredTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly MetricsQuery Period = new() { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 11, 11) };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MetricsMaturityAndUnansweredTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static DateTime Utc(int month, int day, int hour = 15) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    private async Task<MetricsTenant> SeedMaturityAsync()
    {
        var t = await MetricsTenant.StartAsync(_factory, _client);
        var s = _factory.Services;

        // Matured (started on or before 3 Nov 12:00 UTC): 2 won, 6 lost, 1 gone cold (no message since long ago), 1 undecided but alive.
        for (var i = 0; i < 2; i++)
        {
            await t.SeedAsync(s, false, Utc(10, 5 + i), [In(0), Out(60)], stage: OpportunityStage.Won);
        }

        for (var i = 0; i < 6; i++)
        {
            await t.SeedAsync(s, false, Utc(10, 10 + i), [In(0), Out(60)], stage: OpportunityStage.Lost, loss: LossReason.Price);
        }

        await t.SeedAsync(s, false, Utc(10, 20), [In(0), Out(60)], stage: OpportunityStage.InFollowUp); // cold: last message 21 days old
        await t.SeedAsync(s, false, Utc(11, 2), [In(0), Out(60), In(6 * 86_400)], stage: OpportunityStage.InFollowUp); // alive: wrote again on 8 Nov

        // Immature (started after 3 Nov 12:00 UTC): a fresh tail of 5 already won, 1 lost.
        for (var i = 0; i < 5; i++)
        {
            await t.SeedAsync(s, false, Utc(11, 8, 10 + i), [In(0), Out(60)], stage: OpportunityStage.Won);
        }

        await t.SeedAsync(s, false, Utc(11, 9), [In(0), Out(60)], stage: OpportunityStage.Lost, loss: LossReason.Price);
        return t;
    }

    [Fact]
    public async Task TheRate_IsComputedOverTheMaturedCohort_AndTheFreshTailIsOnlyCounted()
    {
        using var t = await SeedMaturityAsync();
        using var h = new MetricsHarness(_factory.Services, t.TenantId, Now);

        var totals = (await h.Service.GetConversionAsync(Period)).Totals;

        Assert.Equal((16, 7, 7), (totals.Total, totals.Won, totals.Lost)); // the raw counts include the fresh tail
        Assert.Equal((10, 6, 2, 6), (totals.Matured, totals.Immature, totals.MaturedWon, totals.MaturedLost));
        Assert.Equal((1, 9), (totals.Stale, totals.Decided)); // 2 + 6 + the one gone cold; the alive one waits
        Assert.Equal(22.2, totals.ConversionRate); // 2 of 9; counting the tail would say 7 of 15 = 46.7
        Assert.Equal(43.8, totals.WinShareOfAll); // 7 of 16: the raw share keeps the tail
        Assert.False(totals.InMaturation);
    }

    [Fact]
    public async Task APeriodWithNothingMatureYet_HasNoRate_AndIsInMaturation()
    {
        using var t = await SeedMaturityAsync();
        using var h = new MetricsHarness(_factory.Services, t.TenantId, Now);

        var totals = (await h.Service.GetConversionAsync(new MetricsQuery { From = new DateOnly(2026, 11, 8), To = new DateOnly(2026, 11, 11) })).Totals;

        Assert.Equal((6, 6, 0), (totals.Total, totals.Immature, totals.Matured));
        Assert.Null(totals.ConversionRate);
        Assert.True(totals.InMaturation);
        Assert.Equal(83.3, totals.WinShareOfAll); // 5 of 6
    }

    [Fact]
    public async Task ARealDrop_RaisesTheAlert_EvenWhenTheFreshTailWasWonAtOnce()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        var s = _factory.Services;
        // Previous period (October, entirely mature): 30 won of 100. Current period (the first days of November up to the cutoff plus a fresh tail):
        // 20 won of 100 among the mature ones, and 10 more won in the last two days.
        for (var i = 0; i < 100; i++)
        {
            await t.SeedAsync(s, false, Utc(9, 15).AddMinutes(i), [In(0), Out(60)], stage: i < 30 ? OpportunityStage.Won : OpportunityStage.Lost, loss: i < 30 ? null : LossReason.Price);
        }

        for (var i = 0; i < 100; i++)
        {
            await t.SeedAsync(s, false, Utc(10, 12).AddMinutes(i), [In(0), Out(60)], stage: i < 20 ? OpportunityStage.Won : OpportunityStage.Lost, loss: i < 20 ? null : LossReason.Price);
        }

        for (var i = 0; i < 10; i++)
        {
            await t.SeedAsync(s, false, Utc(11, 9).AddMinutes(i), [In(0), Out(60)], stage: OpportunityStage.Won);
        }

        using var h = new MetricsHarness(_factory.Services, t.TenantId, Now);

        // The current period is 10 Oct to 10 Nov (31 days: the previous one is 9 Sep to 10 Oct, which holds the first 100).
        var alerts = await h.Service.GetAlertsAsync(new MetricsQuery { From = new DateOnly(2026, 10, 10), To = new DateOnly(2026, 11, 10) });

        var drop = Assert.Single(alerts.Alerts, a => a.Code == AlertCodes.ConversionDrop);
        Assert.Equal((20.0, 30.0, 10.0), (drop.Values["currentRate"], drop.Values["previousRate"], drop.Values["dropPoints"]));
    }

    // ---- Unanswered: open and closed -----------------------------------------------------------------------------------

    [Fact]
    public async Task Unanswered_IsSplitIntoOpen_WhereSomebodyCanStillAnswer_AndClosed_WhereNobodyDid()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        var s = _factory.Services;
        await t.SeedAsync(s, false, Utc(11, 1), [In(0), Out(300)]); // answered
        await t.SeedAsync(s, false, Utc(11, 2), [In(0)]); // open, unanswered, 8 days
        await t.SeedAsync(s, false, Utc(11, 5), [In(0)]); // open, unanswered, 5 days
        await t.SeedAsync(s, false, Utc(10, 20), [In(0)], closed: true); // closed without a reply (the oldest of all: 21 days)
        await t.SeedAsync(s, false, Utc(10, 21), [In(0)], closed: true); // closed without a reply
        await t.SeedAsync(s, false, Utc(10, 22), [In(0), Out(7200)], closed: true); // closed, but it was answered
        using var h = new MetricsHarness(_factory.Services, t.TenantId, Now);

        var response = await h.Service.GetFirstResponseAsync(Period);

        var totals = response.Totals;
        Assert.Equal((6, 2, 2, 2), (totals.Conversations, totals.Answered, totals.UnansweredOpen, totals.UnansweredClosed));
        // The oldest waiting conversation is the oldest OPEN one (2 Nov 15:00 -> 10 Nov 12:00 = 7 d 21 h): the closed ones are not waiting for anybody.
        Assert.Equal(11_340.0, totals.OldestUnansweredOpenMinutes);
        // SLA: 2 answered (one in 5 min, one in 120) + 2 open past the SLA + 2 closed misses = 6 eligible, 1 on time.
        Assert.Equal((2, 6, 16.7), (totals.UnansweredOpenOverSla, totals.SlaEligible, totals.AnsweredWithinSlaPercent));
    }

    [Fact]
    public async Task TheAlerts_ListOnlyTheOpenUnanswered()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        var s = _factory.Services;
        var open = await t.SeedAsync(s, false, Utc(11, 2), [In(0)]);
        await t.SeedAsync(s, false, Utc(10, 20), [In(0)], closed: true);
        using var h = new MetricsHarness(_factory.Services, t.TenantId, Now);

        var alerts = await h.Service.GetAlertsAsync(Period);

        Assert.Equal(1, alerts.Unanswered.Total);
        Assert.Equal([open.ConversationId], alerts.Unanswered.Items.Select(i => i.ConversationId));
    }

    // ---- NoResponse is the system's inference ------------------------------------------------------------------------------

    [Fact]
    public async Task NoResponse_IsAnInferredLine_AndTheStatedReasonsAreRankedWithoutIt()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        var s = _factory.Services;
        for (var i = 0; i < 6; i++)
        {
            await t.SeedAsync(s, false, Utc(10, 5).AddMinutes(i), [In(0), Out(60)], stage: OpportunityStage.Lost, loss: LossReason.NoResponse);
        }

        for (var i = 0; i < 3; i++)
        {
            await t.SeedAsync(s, false, Utc(10, 6).AddMinutes(i), [In(0), Out(60)], stage: OpportunityStage.Lost, loss: LossReason.Price);
        }

        await t.SeedAsync(s, false, Utc(10, 7), [In(0), Out(60)], stage: OpportunityStage.Lost, loss: LossReason.Timing);
        using var h = new MetricsHarness(_factory.Services, t.TenantId, Now);

        var losses = await h.Service.GetLossReasonsAsync(Period);
        var summary = await h.Service.GetSummaryAsync(Period);

        Assert.Equal((10, 4, 6, 60.0), (losses.TotalLost, losses.StatedLost, losses.NoResponse.Count, losses.NoResponse.SharePercent));
        Assert.Equal([("Price", 3, 75.0), ("Timing", 1, 25.0)], losses.TopReasons.Select(r => (r.Reason, r.Count, r.SharePercent)));
        Assert.Equal((10, 4, 6), (summary.TotalLost, summary.StatedLost, summary.NoResponse.Count));
        Assert.Equal(["Price", "Timing"], summary.TopLossReasons.Select(r => r.Reason));
    }
}
