using System.Text.Json;
using Conversia.Application.Ingestion;
using Conversia.Domain.Enums;
using Conversia.WebhookSimulator.Generation;

namespace Conversia.WebhookSimulator.UnitTests;

public class ScenarioTests
{
    private static readonly ChannelIdentity Channel = new("573005550101", "106540352242922");

    private static ScenarioContext Context(int seed = 7) => new(Channel, "Centro", TimeProvider.System.GetUtcNow().UtcDateTime, seed);

    private static IEnumerable<DateTime> InstantsOf(IEnumerable<PlannedDelivery> deliveries)
        => deliveries.SelectMany(d => Dialog360PayloadParser.Parse(d.Json).Messages).Select(m => m.SentAt);

    [Fact]
    public void Every_scenario_is_known_and_builds_deliveries_the_product_accepts()
    {
        foreach (var name in Scenarios.Names)
        {
            var deliveries = Scenarios.Build(name, Context());

            Assert.NotEmpty(deliveries);
            Assert.All(deliveries, d =>
            {
                using var document = JsonDocument.Parse(d.Json);
                Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
                Assert.False(string.IsNullOrWhiteSpace(d.Description));
                Assert.False(string.IsNullOrWhiteSpace(d.Expected));
            });
        }
    }

    [Fact]
    public void The_same_seed_builds_the_same_bytes_and_another_seed_another_customer()
    {
        var context = Context();

        var first = Scenarios.Build("booking", context).Select(d => d.Json).ToList();
        var second = Scenarios.Build("booking", context).Select(d => d.Json).ToList();
        var other = Scenarios.Build("booking", context with { Seed = 8 }).Select(d => d.Json).ToList();

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Theory]
    [InlineData("booking")]
    [InlineData("price-objection")]
    [InlineData("unanswered")]
    [InlineData("ad-origin")]
    [InlineData("bsuid")]
    [InlineData("duplicate")]
    [InlineData("out-of-order")]
    [InlineData("all")]
    public void Nothing_is_ever_dated_in_the_future_or_before_the_product_accepts(string name)
    {
        var context = Context();

        var instants = InstantsOf(Scenarios.Build(name, context)).ToList();

        Assert.All(instants, instant =>
        {
            Assert.True(instant <= context.Now, $"{name}: {instant:O} is after now ({context.Now:O}).");
            Assert.True(instant >= context.Now.AddHours(-3), $"{name}: {instant:O} is too old for a scenario.");
        });
    }

    [Fact]
    public void Booking_is_a_customer_and_an_advisor_talking_message_by_message()
    {
        var deliveries = Scenarios.Build("booking", Context());

        var messages = deliveries.Select(d => Dialog360PayloadParser.Parse(d.Json).Messages.Single()).ToList();

        Assert.Contains(messages, m => m.Direction == MessageDirection.Inbound);
        Assert.Contains(messages, m => m.Direction == MessageDirection.Outbound);
        Assert.Equal(messages.Select(m => m.SentAt).OrderBy(x => x), messages.Select(m => m.SentAt));
        Assert.Equal(messages.Count, messages.Select(m => m.ProviderMessageId).Distinct().Count());
        Assert.Contains("quedó agendada", messages.Last(m => m.Direction == MessageDirection.Outbound).Text);
    }

    [Fact]
    public void Unanswered_is_only_the_customer_and_waited_longer_than_the_sla()
    {
        var context = Context();

        var messages = Scenarios.Build("unanswered", context).SelectMany(d => Dialog360PayloadParser.Parse(d.Json).Messages).ToList();

        Assert.All(messages, m => Assert.Equal(MessageDirection.Inbound, m.Direction));
        Assert.True(context.Now - messages.Min(m => m.SentAt) >= TimeSpan.FromMinutes(40));
    }

    [Fact]
    public void Ad_origin_carries_the_referral_and_price_objection_does_not()
    {
        var ad = Scenarios.Build("ad-origin", Context()).First();
        var organic = Scenarios.Build("price-objection", Context()).First();

        Assert.NotNull(Dialog360PayloadParser.Parse(ad.Json).Messages.Single().Referral);
        Assert.Null(Dialog360PayloadParser.Parse(organic.Json).Messages.Single().Referral);
    }

    [Fact]
    public void Bsuid_is_the_delivery_the_product_skips()
    {
        var delivery = Assert.Single(Scenarios.Build("bsuid", Context()));

        var parsed = Dialog360PayloadParser.Parse(delivery.Json);

        Assert.Empty(parsed.Messages);
        Assert.Equal(1, parsed.SkippedCount);
        Assert.Contains("skipped", delivery.Expected);
    }

    [Fact]
    public void Duplicate_sends_the_same_body_twice_and_then_the_same_message_in_another_body()
    {
        var deliveries = Scenarios.Build("duplicate", Context());

        Assert.Equal(3, deliveries.Count);
        Assert.Equal(deliveries[0].Json, deliveries[1].Json);
        Assert.NotEqual(deliveries[0].Json, deliveries[2].Json);
        Assert.Equal(Dialog360PayloadParser.Parse(deliveries[0].Json).Messages.Single().ProviderMessageId, Dialog360PayloadParser.Parse(deliveries[2].Json).Messages.Single().ProviderMessageId);
    }

    [Fact]
    public void Out_of_order_arrives_in_the_opposite_order_of_the_message_times()
    {
        var deliveries = Scenarios.Build("out-of-order", Context());

        var arrival = deliveries.Select(d => Dialog360PayloadParser.Parse(d.Json).Messages.Single()).ToList();

        Assert.Equal(3, arrival.Count);
        Assert.NotEqual(arrival.Select(m => m.SentAt).OrderBy(x => x), arrival.Select(m => m.SentAt));
        Assert.Equal(arrival.Select(m => m.SentAt).OrderBy(x => x).Last(), arrival[0].SentAt); // the last message arrives first
        Assert.Equal(arrival.Select(m => m.SentAt).OrderBy(x => x).First(), arrival[^1].SentAt); // the first arrives last
        Assert.Equal(MessageDirection.Outbound, arrival[1].Direction);
        Assert.Equal(TimeSpan.FromMinutes(2), arrival[1].SentAt - arrival[2].SentAt);
    }

    [Fact]
    public void All_runs_every_scenario_with_customers_of_its_own()
    {
        var deliveries = Scenarios.Build("all", Context());

        var customers = deliveries.SelectMany(d => Dialog360PayloadParser.Parse(d.Json).Messages).Select(m => m.ContactWaId).Distinct().ToList();

        Assert.True(deliveries.Count > Scenarios.Names.Length);
        Assert.True(customers.Count >= 6, $"Only {customers.Count} different customers.");
    }

    [Fact]
    public void An_unknown_scenario_is_refused_with_the_list_of_the_good_ones()
    {
        var exception = Assert.Throws<ArgumentException>(() => Scenarios.Build("nonsense", Context()));

        Assert.Contains("booking", exception.Message);
    }
}
