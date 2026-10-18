using Conversia.Application.DTOs.Metrics;
using Conversia.Application.Metrics;
using Conversia.Application.Time;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// A few thousand generated conversations, checked against an oracle: the same numbers worked out in memory, in plain LINQ, from the list
/// the conversations were generated from. It proves the SQL aggregation agrees with the definitions of docs/METRICS.md at a size where a
/// mistake in a filter, a group or a percentile would show; it measures correctness, not speed. Dates are absolute and the clock is fixed.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class MetricsLargeDatasetTests
{
    private const int Count = 3000;
    private static readonly DateTime Base = new(2026, 6, 1, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantTimeZone Zone = new("America/Bogota");
    private static readonly DateOnly From = new(2026, 7, 1);
    private static readonly DateOnly To = new(2026, 8, 15);

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MetricsLargeDatasetTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private sealed record Row(
        int Index, bool North, Guid? AdvisorId, ConversationSource Source, DateTime Start, int? ResponseSeconds, DateTime LastMessageAt,
        OpportunityStage Stage, LossReason? Loss, ObjectionType Objection, List<(MessageDirection Direction, DateTime At)> Messages);

    private static List<Row> Generate(MetricsTenant t)
    {
        var rows = new List<Row>(Count);
        for (var i = 0; i < Count; i++)
        {
            var north = i % 2 == 1;
            var start = Base.AddMinutes(i * 47);
            int? response = i % 13 == 0 ? null : 30 + ((i * 53) % 7200);
            var messages = new List<(MessageDirection, DateTime)> { (MessageDirection.Inbound, start) };
            if (response is { } seconds)
            {
                messages.Add((MessageDirection.Outbound, start.AddSeconds(seconds)));
            }

            if (i % 5 == 0)
            {
                messages.Add((MessageDirection.Inbound, start.AddDays(2)));
            }

            var stage = (i % 10) switch { <= 3 => OpportunityStage.Won, <= 6 => OpportunityStage.Lost, 8 => OpportunityStage.New, _ => OpportunityStage.InFollowUp };
            LossReason? loss = stage == OpportunityStage.Lost ? (i % 3) switch { 0 => LossReason.Price, 1 => LossReason.Timing, _ => LossReason.Location } : null;
            var objection = (i % 6) switch { 0 => ObjectionType.Price, 1 => ObjectionType.Pain, _ => ObjectionType.None };
            var source = i % 7 == 0 ? ConversationSource.Ads : i % 11 == 0 ? ConversationSource.Unknown : ConversationSource.Organic;
            Guid? advisor = i % 5 == 0 ? null : north ? t.Beto.Id : t.Ana.Id;
            rows.Add(new Row(i, north, advisor, source, start, response, messages.Max(m => m.Item2), stage, loss, objection, messages));
        }

        return rows;
    }

    private async Task SeedAsync(MetricsTenant t, List<Row> rows)
    {
        foreach (var chunk in rows.Chunk(400))
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach (var row in chunk)
            {
                var channelId = row.North ? t.SecondChannel.Id : t.Fixture.Channel.Id;
                var contact = Contact.Create(t.TenantId, UniqueWaId(), "Cliente", row.Start);
                var conversation = Conversation.Start(t.TenantId, contact.Id, channelId, row.North ? t.Second.Id : t.Centro.Id, row.Start, row.Source);
                if (row.AdvisorId is { } advisor)
                {
                    conversation.AssignAdvisor(advisor);
                }

                var messages = row.Messages
                    .Select(m => Message.Create(t.TenantId, conversation.Id, channelId, $"wamid.{Guid.NewGuid():N}", m.Direction, "text", null, m.At))
                    .ToList();
                conversation.ApplyTimeline(ConversationTimeline.From(messages.Select(m => (m.Direction, m.SentAt)))!);
                var opportunity = Opportunity.Open(t.TenantId, conversation.Id);
                MetricsTenant.Decide(opportunity, row.Start, row.Stage, row.Loss, row.Objection);
                db.AddRange(contact, conversation, opportunity);
                db.AddRange(messages);
            }

            await db.SaveChangesAsync();
        }
    }

    // ---- The oracle -------------------------------------------------------------------------------------------------

    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);

    private static double? Percent(int part, int whole) => whole == 0 ? null : Round(100.0 * part / whole, 1);

    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        var position = p * (sorted.Count - 1);
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        return sorted[low] + ((position - low) * (sorted[high] - sorted[low]));
    }

    private static DateTime FromUtc => Zone.StartOfDayUtc(From);

    private static DateTime ToUtc => Zone.StartOfDayUtc(To);

    private static bool InCohort(Row r) => r.Start >= FromUtc && r.Start < ToUtc;

    private static bool IsStale(Row r) => r.Stage is OpportunityStage.InFollowUp or OpportunityStage.New && r.LastMessageAt < Now.AddDays(-7);

    private static void AssertConversion(IEnumerable<Row> cohort, ConversionCounts actual)
    {
        var rows = cohort.ToList();
        var won = rows.Count(r => r.Stage == OpportunityStage.Won);
        var lost = rows.Count(r => r.Stage == OpportunityStage.Lost);
        var stale = rows.Count(IsStale);
        var decided = won + lost + stale;
        Assert.Equal(
            (rows.Count, won, lost, rows.Count(r => r.Stage == OpportunityStage.InFollowUp), rows.Count(r => r.Stage == OpportunityStage.New), stale, decided, Percent(won, decided), Percent(won, rows.Count)),
            (actual.Total, actual.Won, actual.Lost, actual.InFollowUp, actual.New, actual.Stale, actual.Decided, actual.ConversionRate, actual.WinShareOfAll));
    }

    private static void AssertResponse(IEnumerable<Row> cohort, FirstResponseStats actual)
    {
        var rows = cohort.ToList();
        var seconds = rows.Where(r => r.ResponseSeconds is not null).Select(r => (double)r.ResponseSeconds!.Value).Order().ToList();
        var unanswered = rows.Where(r => r.ResponseSeconds is null).ToList();
        var overSla = unanswered.Count(r => r.Start < Now.AddMinutes(-30));
        var within = seconds.Count(s => s <= 30 * 60);

        Assert.Equal((rows.Count, seconds.Count, unanswered.Count, within, overSla, seconds.Count + overSla), (actual.Conversations, actual.Answered, actual.Unanswered, actual.WithinSla, actual.UnansweredOverSla, actual.SlaEligible));
        Assert.Equal(Percent(within, seconds.Count + overSla), actual.AnsweredWithinSlaPercent);
        if (seconds.Count == 0)
        {
            Assert.Null(actual.AvgMinutes);
            Assert.Null(actual.MedianMinutes);
            Assert.Null(actual.P90Minutes);
        }
        else
        {
            Assert.InRange(actual.AvgMinutes!.Value, (seconds.Average() / 60) - 0.011, (seconds.Average() / 60) + 0.011);
            Assert.InRange(actual.MedianMinutes!.Value, (Percentile(seconds, 0.5) / 60) - 0.011, (Percentile(seconds, 0.5) / 60) + 0.011);
            Assert.InRange(actual.P90Minutes!.Value, (Percentile(seconds, 0.9) / 60) - 0.011, (Percentile(seconds, 0.9) / 60) + 0.011);
        }

        if (unanswered.Count == 0)
        {
            Assert.Null(actual.OldestUnansweredMinutes);
        }
        else
        {
            Assert.Equal(Round((Now - unanswered.Min(r => r.Start)).TotalMinutes, 2), actual.OldestUnansweredMinutes);
        }
    }

    [Fact]
    public async Task EveryMetric_AgreesWithTheOracle_OverThreeThousandConversations()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        var rows = Generate(t);
        await SeedAsync(t, rows);
        // Noise of another tenant in the same period must change nothing.
        using var rival = await MetricsTenant.StartAsync(_factory, _client);
        await SeedAsync(rival, Generate(rival).Take(300).ToList());
        using var h = new MetricsHarness(_factory.Services, t.TenantId, Now);
        var query = new MetricsQuery { From = From, To = To };
        var cohort = rows.Where(InCohort).ToList();
        Assert.InRange(cohort.Count, 1000, 1700); // the window is a good slice of the dataset (45 of its 98 days)

        // Conversion: totals and the three breakdowns.
        var conversion = await h.Service.GetConversionAsync(query);
        AssertConversion(cohort, conversion.Totals);
        Assert.Equal(2, conversion.ByBranch.Count);
        AssertConversion(cohort.Where(r => !r.North), conversion.ByBranch.Single(b => b.Id == t.Centro.Id));
        AssertConversion(cohort.Where(r => r.North), conversion.ByBranch.Single(b => b.Id == t.Second.Id));
        Assert.Equal(3, conversion.ByAdvisor.Count);
        AssertConversion(cohort.Where(r => r.AdvisorId is null), conversion.ByAdvisor.Single(a => a.Id is null));
        AssertConversion(cohort.Where(r => r.AdvisorId == t.Ana.Id), conversion.ByAdvisor.Single(a => a.Id == t.Ana.Id));
        AssertConversion(cohort.Where(r => r.AdvisorId == t.Beto.Id), conversion.ByAdvisor.Single(a => a.Id == t.Beto.Id));
        Assert.Equal(3, conversion.BySource.Count);
        foreach (var source in Enum.GetValues<ConversationSource>())
        {
            AssertConversion(cohort.Where(r => r.Source == source), conversion.BySource.Single(s => s.Name == source.ToString()));
        }

        // First response: totals and the breakdowns.
        var response = await h.Service.GetFirstResponseAsync(query);
        AssertResponse(cohort, response.Totals);
        AssertResponse(cohort.Where(r => !r.North), response.ByBranch.Single(b => b.Id == t.Centro.Id));
        AssertResponse(cohort.Where(r => r.North), response.ByBranch.Single(b => b.Id == t.Second.Id));
        AssertResponse(cohort.Where(r => r.AdvisorId is null), response.ByAdvisor.Single(a => a.Id is null));
        AssertResponse(cohort.Where(r => r.AdvisorId == t.Ana.Id), response.ByAdvisor.Single(a => a.Id == t.Ana.Id));
        foreach (var source in Enum.GetValues<ConversationSource>())
        {
            AssertResponse(cohort.Where(r => r.Source == source), response.BySource.Single(s => s.Name == source.ToString()));
        }

        // Loss reasons and objections.
        var losses = await h.Service.GetLossReasonsAsync(query);
        var lostRows = cohort.Where(r => r.Stage == OpportunityStage.Lost).ToList();
        Assert.Equal((cohort.Count, lostRows.Count, 0), (losses.Conversations, losses.TotalLost, losses.OtherCount));
        Assert.Equal(
            lostRows.GroupBy(r => r.Loss!.Value.ToString()).Select(g => (g.Key, g.Count(), Percent(g.Count(), lostRows.Count)!.Value)).OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.Ordinal),
            losses.TopReasons.Select(r => (r.Reason, r.Count, r.SharePercent)));
        foreach (var objection in losses.Objections)
        {
            var expected = cohort.Count(r => r.Objection.ToString() == objection.Objection);
            Assert.Equal((expected, Percent(expected, cohort.Count) ?? 0), (objection.Count, objection.SharePercent));
        }

        // Volume: conversations per day and per hour of the week, messages per day.
        var volume = await h.Service.GetVolumeAsync(query);
        var localStarts = cohort.Select(r => TimeZoneInfo.ConvertTimeFromUtc(r.Start, Zone.Zone)).ToList();
        Assert.Equal(45, volume.ByDay.Count);
        foreach (var day in volume.ByDay)
        {
            var inbound = rows.SelectMany(r => r.Messages).Count(m => m.Direction == MessageDirection.Inbound && m.At >= FromUtc && m.At < ToUtc && DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(m.At, Zone.Zone)) == day.Date);
            var outbound = rows.SelectMany(r => r.Messages).Count(m => m.Direction == MessageDirection.Outbound && m.At >= FromUtc && m.At < ToUtc && DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(m.At, Zone.Zone)) == day.Date);
            Assert.Equal((localStarts.Count(s => DateOnly.FromDateTime(s) == day.Date), inbound, outbound), (day.Conversations, day.InboundMessages, day.OutboundMessages));
        }

        foreach (var cell in volume.ByHourOfWeek)
        {
            var isoDay = ((int)new DateTime(2026, 1, 5).AddDays(cell.DayOfWeek - 1).DayOfWeek + 6) % 7 + 1; // 1 = Monday
            Assert.Equal(cell.DayOfWeek, isoDay);
            var expectedConversations = localStarts.Count(s => IsoDay(s) == cell.DayOfWeek && s.Hour == cell.Hour);
            var expectedInbound = rows.SelectMany(r => r.Messages)
                .Where(m => m.Direction == MessageDirection.Inbound && m.At >= FromUtc && m.At < ToUtc)
                .Select(m => TimeZoneInfo.ConvertTimeFromUtc(m.At, Zone.Zone))
                .Count(s => IsoDay(s) == cell.DayOfWeek && s.Hour == cell.Hour);
            Assert.Equal((expectedConversations, expectedInbound), (cell.Conversations, cell.InboundMessages));
        }

        Assert.Equal(cohort.Count, volume.Conversations);

        // The same filters the API takes, against the oracle.
        var filtered = await h.Service.GetConversionAsync(new MetricsQuery { From = From, To = To, BranchId = t.Second.Id, Source = "Ads" });
        AssertConversion(cohort.Where(r => r.North && r.Source == ConversationSource.Ads), filtered.Totals);
        var unassigned = await h.Service.GetFirstResponseAsync(new MetricsQuery { From = From, To = To, Unassigned = true });
        AssertResponse(cohort.Where(r => r.AdvisorId is null), unassigned.Totals);
    }

    private static int IsoDay(DateTime local) => local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;
}
