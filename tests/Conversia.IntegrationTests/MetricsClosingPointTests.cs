using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Metrics;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// "Hasta dónde llegan las conversaciones" (docs/METRICS.md, "Closing point") against a real PostgreSQL: mixed data, the filters of every
/// metric, another tenant's data and the sandbox, all worked out by hand. October 2026 in Bogota; now is 10 November (maturity 7 days, so an
/// undecided conversation is stopped when its last message is before 3 November 12:00 UTC).
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class MetricsClosingPointTests(CustomWebApplicationFactory factory)
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static MetricsQuery October(Action<MetricsQuery>? filter = null)
    {
        var query = new MetricsQuery { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 11, 1) };
        filter?.Invoke(query);
        return query;
    }
    private readonly HttpClient _client = factory.CreateClient();

    private static DateTime Utc(int month, int day) => new(2026, month, day, 15, 0, 0, DateTimeKind.Utc);

    private static Action<Opportunity> Point(OpportunityStage stage, ReachedStage? reached, OffChannelMove move = OffChannelMove.None, LossReason? loss = null)
        => o =>
        {
            if (stage != OpportunityStage.New)
            {
                o.SetManualOutcome(stage, stage == OpportunityStage.Lost ? loss ?? LossReason.Price : null, null, Utc(10, 1));
            }

            if (reached is not null || move != OffChannelMove.None)
            {
                o.ApplyClassification(new ClassificationUpdate(null, null, ObjectionType.None, null, null, 0.5m, null,
                    ClosingPoint: new ClosingPointReading(reached, move, move == OffChannelMove.None ? null : 2, ClosedBy.Unknown, false)), Utc(10, 1));
            }
        };

    private async Task<MetricsTenant> SeedAsync()
    {
        var t = await MetricsTenant.StartAsync(factory, _client);
        var s = factory.Services;
        Task Seed(bool north, DateTime start, IReadOnlyList<Msg> messages, Action<Opportunity> point, ConversationSource source = ConversationSource.Organic, Guid? advisor = null, bool sandbox = false)
            => SeedConversationAsync(s, t.TenantId, north ? t.Second.Id : t.Centro.Id, north ? t.SecondChannel.Id : t.Fixture.Channel.Id, start, messages, source, advisor,
                configureOpportunity: point, sandbox: sandbox);

        await Seed(false, Utc(10, 5), [In(0), Out(60)], Point(OpportunityStage.Lost, ReachedStage.PriceShared, OffChannelMove.PhoneCall), ConversationSource.Ads, t.Ana.Id); // a: stopped at price, moved by phone
        await Seed(false, Utc(10, 6), [In(0), Out(60)], Point(OpportunityStage.InFollowUp, ReachedStage.ObjectionRaised), advisor: t.Ana.Id); // b: gone cold at the objection
        await Seed(true, Utc(10, 7), [In(0), Out(60)], Point(OpportunityStage.Won, ReachedStage.AppointmentBooked, OffChannelMove.InPerson), advisor: t.Beto.Id); // c: won, not stopped
        await Seed(true, Utc(10, 30), [In(0), In(10 * 86_400)], Point(OpportunityStage.InFollowUp, ReachedStage.PriceShared), advisor: t.Beto.Id); // d: alive on 9 Nov
        await Seed(false, Utc(10, 8), [In(0)], Point(OpportunityStage.New, null)); // e: never read, gone cold
        await Seed(true, Utc(10, 9), [In(0), Out(30)], Point(OpportunityStage.Lost, ReachedStage.PriceShared), ConversationSource.Ads, t.Beto.Id); // f: stopped at price

        // Never counted: a sandbox conversation of the same tenant, one outside the period, one the business started (no customer message).
        await Seed(false, Utc(10, 10), [In(0)], Point(OpportunityStage.Lost, ReachedStage.FirstContact, OffChannelMove.OtherChannel), sandbox: true);
        await Seed(false, Utc(11, 2), [In(0)], Point(OpportunityStage.Lost, ReachedStage.FirstContact));
        await Seed(false, Utc(10, 11), [Out(0)], Point(OpportunityStage.Lost, ReachedStage.FirstContact));
        return t;
    }

    private async Task<ClosingPointResponse> GetAsync(MetricsTenant t, MetricsQuery query)
    {
        using var harness = new MetricsHarness(factory.Services, t.TenantId, Now);
        return await harness.Service.GetClosingPointAsync(query);
    }

    private static (string Stage, int Count)[] NonZero(ClosingPointResponse r) => [.. r.Stages.Where(s => s.Count > 0).Select(s => (s.Stage, s.Count))];

    [Fact]
    public async Task TheStoppedConversations_ByStage_AndTheMoveOffTheChat()
    {
        using var t = await SeedAsync();
        using var other = await SeedAsync(); // another tenant with the same data: never mixed in

        var r = await GetAsync(t, October());

        Assert.Equal((6, 4, 1), (r.Conversations, r.Stopped, r.NotRead)); // a, b, e, f stopped; e unread
        Assert.Equal([("PriceShared", 2), ("ObjectionRaised", 1)], NonZero(r));
        Assert.Equal(66.7, r.Stages.Single(s => s.Stage == "PriceShared").SharePercent);
        Assert.Equal("PriceShared", r.MainDropOff);
        Assert.Equal((2, (double?)33.3), (r.MovedOffChannel.Count, r.MovedOffChannel.SharePercent)); // a (phone) and c (in person) of 6
        Assert.Equal([("PhoneCall", 1), ("InPerson", 1), ("OtherChannel", 0)], r.MovedOffChannel.ByMove.Select(m => (m.Move, m.Count)));
    }

    [Fact]
    public async Task TheFilters_OfEveryMetric_Apply()
    {
        using var t = await SeedAsync();

        var centro = await GetAsync(t, October(q => q.BranchId = t.Centro.Id));
        var beto = await GetAsync(t, October(q => q.AdvisorId = t.Beto.Id));
        var ads = await GetAsync(t, October(q => q.Source = "Ads"));
        var unassigned = await GetAsync(t, October(q => q.Unassigned = true));

        Assert.Equal((3, 3), (centro.Conversations, centro.Stopped));
        Assert.Equal([("PriceShared", 1), ("ObjectionRaised", 1)], NonZero(centro));
        Assert.Equal((3, 1), (beto.Conversations, beto.Stopped));
        Assert.Equal([("PriceShared", 1)], NonZero(beto));
        Assert.Equal((2, 2, 1), (ads.Conversations, ads.Stopped, ads.MovedOffChannel.Count));
        Assert.Equal((1, 1, 1, (string?)null), (unassigned.Conversations, unassigned.Stopped, unassigned.NotRead, unassigned.MainDropOff));
    }

    [Fact]
    public async Task TheEndpoint_AnswersWithTheMetricsPermission()
    {
        using var t = await SeedAsync();

        var response = await t.Owner.GetAsync("/api/metrics/closing-point?from=2026-10-01&to=2026-11-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ClosingPointResponse>();
        Assert.Equal(7, body!.Stages.Count);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/metrics/closing-point")).StatusCode);
    }
}
