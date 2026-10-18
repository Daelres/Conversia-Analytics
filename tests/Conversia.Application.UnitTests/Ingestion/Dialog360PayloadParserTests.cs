using Conversia.Application.Ingestion;
using Conversia.Domain.Enums;
using Conversia.Tests.Shared;

namespace Conversia.Application.UnitTests.Ingestion;

/// <summary>The parser against the documented sample deliveries (the echo ones are unverified against a real sandbox, see docs/WEBHOOKS.md).</summary>
public class Dialog360PayloadParserTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void InboundText_MapsEveryField()
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.InboundText());

        var message = Assert.Single(parsed.Messages);
        Assert.Equal(Dialog360Samples.PhoneNumberId, message.PhoneNumberId);
        Assert.Equal(Dialog360Samples.CustomerWaId, message.ContactWaId);
        Assert.Equal("Laura Gómez", message.ContactName);
        Assert.Equal("wamid.HBgM-IN-1", message.ProviderMessageId);
        Assert.Equal(MessageDirection.Inbound, message.Direction);
        Assert.Equal("text", message.Type);
        Assert.Equal("Hola, quisiera info de depilación láser", message.Text);
        Assert.Equal(T0, message.SentAt);
        Assert.Equal(DateTimeKind.Utc, message.SentAt.Kind);
        Assert.Null(message.Referral);
        Assert.Equal((0, 0), (parsed.StatusCount, parsed.SkippedCount));
    }

    [Fact]
    public void TwoMessagesInOneDelivery_AreBothReturnedInPayloadOrder()
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.TwoInboundMessages());

        Assert.Equal(["wamid.HBgM-IN-A", "wamid.HBgM-IN-B"], parsed.Messages.Select(m => m.ProviderMessageId));
        Assert.Equal(T0.AddSeconds(5), parsed.Messages[1].SentAt);
        Assert.All(parsed.Messages, m => Assert.Equal("Laura Gómez", m.ContactName));
    }

    [Fact]
    public void AdReferral_IsReadFromTheMessageAndPrefersTheHeadline()
    {
        var message = Assert.Single(Dialog360PayloadParser.Parse(Dialog360Samples.InboundFromAd()).Messages);

        Assert.Equal("Depilación láser 2x1", message.Referral!.Reference);
    }

    [Fact]
    public void MediaMessage_UsesTheCaptionAsText_AndAnAudioHasNone()
    {
        var image = Assert.Single(Dialog360PayloadParser.Parse(Dialog360Samples.InboundImage()).Messages);
        var audio = Assert.Single(Dialog360PayloadParser.Parse(Dialog360Samples.InboundAudio()).Messages);

        Assert.Equal(("image", "Así quedó"), (image.Type, image.Text));
        Assert.Equal(("audio", (string?)null), (audio.Type, audio.Text));
    }

    [Fact]
    public void StatusesOnly_YieldsNoMessagesAndCountsTheReceipts()
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.StatusesOnly());

        Assert.Empty(parsed.Messages);
        Assert.Equal(2, parsed.StatusCount);
    }

    [Fact]
    public void Echo_IsAnOutboundMessageToTheCustomer()
    {
        var message = Assert.Single(Dialog360PayloadParser.Parse(Dialog360Samples.OutboundEcho()).Messages);

        Assert.Equal(MessageDirection.Outbound, message.Direction);
        Assert.Equal(Dialog360Samples.CustomerWaId, message.ContactWaId);
        Assert.Equal("wamid.HBgM-ECHO-1", message.ProviderMessageId);
        Assert.Equal(T0.AddSeconds(60), message.SentAt);
        Assert.Null(message.ContactName);
        Assert.Null(message.Referral);
    }

    [Fact]
    public void AMessageSentByTheBusinessNumberItself_IsOutboundToItsRecipient()
    {
        var json = $$"""
            { "entry": [ { "changes": [ { "value": {
              "metadata": { "display_phone_number": "+57 300 555 0100", "phone_number_id": "1" },
              "messages": [ { "from": "{{Dialog360Samples.BusinessNumber}}", "to": "{{Dialog360Samples.CustomerWaId}}", "id": "wamid.X", "timestamp": "{{Dialog360Samples.T0}}", "type": "text", "text": { "body": "hola" } } ]
            } } ] } ] }
            """;

        var message = Assert.Single(Dialog360PayloadParser.Parse(json).Messages);

        Assert.Equal((MessageDirection.Outbound, Dialog360Samples.CustomerWaId), (message.Direction, message.ContactWaId));
    }

    [Fact]
    public void TheOlderRootShape_IsAlsoUnderstood()
    {
        var message = Assert.Single(Dialog360PayloadParser.Parse(Dialog360Samples.LegacyRootShape()).Messages);

        Assert.Equal((MessageDirection.Inbound, "wamid.LEGACY-1", (string?)null), (message.Direction, message.ProviderMessageId, message.PhoneNumberId));
    }

    [Fact]
    public void ADeliveryOfAnUnmodelledEvent_ParsesToNothing()
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.UnknownEvent());

        Assert.Empty(parsed.Messages);
        Assert.Equal((0, 0), (parsed.StatusCount, parsed.SkippedCount));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"entry\": 5}")]
    [InlineData("{\"entry\": [null, 3, {\"changes\": \"x\"}, {\"changes\": [null, {\"value\": []}]}]}")]
    [InlineData("{\"messages\": \"hola\"}")]
    public void ShapesThatAreNotDeliveries_NeverThrowAndYieldNothing(string json)
        => Assert.Empty(Dialog360PayloadParser.Parse(json).Messages);

    [Theory]
    [InlineData("""{ "from": "573005550101", "timestamp": "1792324800", "type": "text" }""")] // no id
    [InlineData("""{ "id": "a", "timestamp": "1792324800", "type": "text" }""")] // no sender
    [InlineData("""{ "from": "573005550101", "id": "a", "type": "text" }""")] // no timestamp
    [InlineData("""{ "from": "573005550101", "id": "a", "timestamp": "soon", "type": "text" }""")]
    [InlineData("""{ "from": "573005550101", "id": "a", "timestamp": "-5", "type": "text" }""")]
    [InlineData("""{ "from": "12", "id": "a", "timestamp": "1792324800", "type": "text" }""")] // implausible wa_id
    public void AMessageMissingWhatItNeeds_IsSkippedAndCounted(string message)
    {
        var parsed = Dialog360PayloadParser.Parse($$"""{ "messages": [ {{message}} ] }""");

        Assert.Empty(parsed.Messages);
        Assert.Equal(1, parsed.SkippedCount);
    }

    [Fact]
    public void TimestampAsNumber_AndSeparatorsInTheWaId_AreAccepted()
    {
        var json = $$"""{ "messages": [ { "from": "+57 300 555-0101", "id": "a", "timestamp": {{Dialog360Samples.T0}}, "type": "text", "text": { "body": "x" } } ] }""";

        var message = Assert.Single(Dialog360PayloadParser.Parse(json).Messages);

        Assert.Equal(T0, message.SentAt);
        Assert.Equal("+57 300 555-0101", message.ContactWaId); // normalized later, by the contact rules
    }

    [Fact]
    public void UnknownTypeNames_AreKeptLowerCased_AndOddOnesBecomeUnknown()
    {
        var json = $$"""
            { "messages": [
              { "from": "573005550101", "id": "a", "timestamp": "{{Dialog360Samples.T0}}", "type": "Sticker" },
              { "from": "573005550101", "id": "b", "timestamp": "{{Dialog360Samples.T0}}", "type": "weird type!" },
              { "from": "573005550101", "id": "c", "timestamp": "{{Dialog360Samples.T0}}" } ] }
            """;

        Assert.Equal(["sticker", "unknown", "unknown"], Dialog360PayloadParser.Parse(json).Messages.Select(m => m.Type));
    }

    [Fact]
    public void TextIsCutToTheStorageLimit_AndNulCharactersAreDropped()
    {
        var json = $$"""{ "messages": [ { "from": "573005550101", "id": "a", "timestamp": "{{Dialog360Samples.T0}}", "type": "text", "text": { "body": "a\u0000b{{new string('x', 5000)}}" } } ] }""";

        var text = Assert.Single(Dialog360PayloadParser.Parse(json).Messages).Text!;

        Assert.Equal(Domain.Entities.Message.TextMaxLength, text.Length);
        Assert.StartsWith("abxxx", text);
    }

    [Fact]
    public void AnInteractiveReply_UsesItsTitleAsText()
    {
        var json = $$"""{ "messages": [ { "from": "573005550101", "id": "a", "timestamp": "{{Dialog360Samples.T0}}", "type": "interactive", "interactive": { "type": "button_reply", "button_reply": { "id": "1", "title": "Sí, agendar" } } } ] }""";

        Assert.Equal("Sí, agendar", Assert.Single(Dialog360PayloadParser.Parse(json).Messages).Text);
    }
}
