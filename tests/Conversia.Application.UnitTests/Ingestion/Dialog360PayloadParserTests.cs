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
    public void ATimestampAsANumberAndAPlusInTheWaId_AreAccepted()
    {
        var json = $$"""{ "messages": [ { "from": "+573005550101", "id": "a", "timestamp": {{Dialog360Samples.T0}}, "type": "text", "text": { "body": "x" } } ] }""";

        var message = Assert.Single(Dialog360PayloadParser.Parse(json).Messages);

        Assert.Equal(T0, message.SentAt);
        Assert.Equal("573005550101", message.ContactWaId); // normalized by the contact rules
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

    // ---- Identifiers: a BSUID is not a phone number ---------------------------------------------------------

    [Theory]
    [InlineData("CO.13491208655302741918")]
    [InlineData("US.123456")]
    [InlineData("CA.123456")]
    [InlineData("+57 300 555 0101")]
    [InlineData("57300-555-0101")]
    public void AnInboundMessageWithoutAWhatsAppPhoneNumber_IsSkippedAndCounted_NeverTurnedIntoAFakeNumber(string from)
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.InboundFrom(from));

        Assert.Empty(parsed.Messages);
        Assert.Equal(1, parsed.SkippedCount);
    }

    // ---- Timestamps -------------------------------------------------------------------------------------------

    private static ParsedWebhook ParseWithTimestamp(string timestampJson, DateTime? now = null)
        => Dialog360PayloadParser.Parse(
            $$"""{ "messages": [ { "from": "573005550101", "id": "a", "timestamp": {{timestampJson}}, "type": "text", "text": { "body": "x" } } ] }""",
            now ?? T0.AddHours(1));

    [Theory]
    [InlineData("\"1792324800\"")]
    [InlineData("1792324800")]
    [InlineData("\"1792324800.0\"")]
    [InlineData("1792324800.0")]
    [InlineData("1792324800.9")] // truncated to the second
    [InlineData("1.7923248e9")]
    [InlineData("\"1.7923248E9\"")]
    public void TimestampsInAnyNumericSpelling_AreTruncatedToTheSecond(string timestamp)
        => Assert.Equal(T0, Assert.Single(ParseWithTimestamp(timestamp).Messages).SentAt);

    [Theory]
    [InlineData("0")]
    [InlineData("\"3600\"")] // 1970
    [InlineData("1419984000")] // 2014-12-31
    [InlineData("100000000000")] // year 5138
    [InlineData("-1792324800")]
    [InlineData("\"\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"1e400\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void ImplausibleTimestamps_AreSkippedAndCounted(string timestamp)
    {
        var parsed = ParseWithTimestamp(timestamp);

        Assert.Empty(parsed.Messages);
        Assert.Equal(1, parsed.SkippedCount);
    }

    [Fact]
    public void TheWindowIsFrom2015UntilADayAfterNow()
    {
        var now = T0;

        Assert.Single(ParseWithTimestamp("1420070400", now).Messages); // 2015-01-01T00:00:00Z
        Assert.Empty(ParseWithTimestamp("1420070399", now).Messages);
        Assert.Single(ParseWithTimestamp((Dialog360Samples.T0 + 86_400).ToString(), now).Messages); // tolerated clock skew
        Assert.Empty(ParseWithTimestamp((Dialog360Samples.T0 + 86_401).ToString(), now).Messages);
    }

    // ---- Who the customer of an outbound message is ---------------------------------------------------------------

    private static string EchoWithoutTo(string contacts) => $$"""
        { "entry": [ { "changes": [ { "value": {
          "metadata": { "display_phone_number": "{{Dialog360Samples.BusinessNumber}}", "phone_number_id": "1" },
          {{contacts}}
          "message_echoes": [ { "from": "{{Dialog360Samples.BusinessNumber}}", "id": "wamid.E", "timestamp": "{{Dialog360Samples.T0}}", "type": "text", "text": { "body": "hola" } } ]
        } } ] } ] }
        """;

    private const string OneContact = "\"contacts\": [ { \"profile\": { \"name\": \"Laura\" }, \"wa_id\": \"573005550101\" } ],";

    [Fact]
    public void AnOutboundWithoutARecipient_BelongsToTheOnlyContactOfTheDelivery()
    {
        var message = Assert.Single(Dialog360PayloadParser.Parse(EchoWithoutTo(OneContact)).Messages);

        Assert.Equal("573005550101", message.ContactWaId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"contacts\": [ { \"wa_id\": \"573005550101\" }, { \"wa_id\": \"573005550102\" } ],")]
    public void AnOutboundWithoutARecipient_AndNotExactlyOneContact_IsSkippedAndCounted_NeverGuessed(string contacts)
    {
        var parsed = Dialog360PayloadParser.Parse(EchoWithoutTo(contacts));

        Assert.Empty(parsed.Messages);
        Assert.Equal(1, parsed.SkippedCount);
    }

    [Fact]
    public void ABusinessSenderWithoutARecipient_FollowsTheSameRule()
    {
        var json = $$"""
            { "messages": [ { "from": "{{Dialog360Samples.BusinessNumber}}", "id": "a", "timestamp": "{{Dialog360Samples.T0}}", "type": "text" } ],
              "metadata": { "display_phone_number": "{{Dialog360Samples.BusinessNumber}}" },
              "contacts": [ { "wa_id": "573005550101" }, { "wa_id": "573005550102" } ] }
            """;

        var parsed = Dialog360PayloadParser.Parse(json);

        Assert.Empty(parsed.Messages);
        Assert.Equal(1, parsed.SkippedCount);
    }

    // ---- What is not a conversation message ---------------------------------------------------------------------------

    [Theory]
    [InlineData("\"group_id\": \"120363000000000000@g.us\",")]
    [InlineData("\"recipient_type\": \"group\",")]
    public void GroupMessages_AreSkippedAndCounted(string marker)
    {
        var json = $$"""{ "messages": [ { {{marker}} "from": "573005550101", "id": "a", "timestamp": "{{Dialog360Samples.T0}}", "type": "text", "text": { "body": "x" } } ] }""";

        var parsed = Dialog360PayloadParser.Parse(json);

        Assert.Empty(parsed.Messages);
        Assert.Equal(1, parsed.SkippedCount);
    }

    [Theory]
    [InlineData("reaction")]
    [InlineData("system")]
    [InlineData("Reaction")]
    public void ReactionsAndSystemMessages_AreIgnoredNotStored_AndNotCountedAsSkipped(string type)
    {
        var json = $$"""{ "messages": [ { "from": "573005550101", "id": "a", "timestamp": "{{Dialog360Samples.T0}}", "type": "{{type}}" } ] }""";

        var parsed = Dialog360PayloadParser.Parse(json);

        Assert.Empty(parsed.Messages);
        Assert.Equal((0, 1), (parsed.SkippedCount, parsed.IgnoredCount));
    }

    // ---- Ad reference ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "headline": "", "source_url": "https://fb.me/ad" }""", "https://fb.me/ad")]
    [InlineData("""{ "headline": "   ", "source_url": "", "source_id": "123" }""", "123")]
    [InlineData("""{ "headline": " Promo ", "source_url": "https://fb.me/ad" }""", "Promo")]
    [InlineData("""{ "headline": "", "source_url": "" }""", null)]
    [InlineData("""{ }""", null)]
    public void TheAdReference_IsTheFirstNonBlankOfHeadlineUrlIdAndBody(string referral, string? expected)
    {
        var json = $$"""{ "messages": [ { "from": "573005550101", "id": "a", "timestamp": "{{Dialog360Samples.T0}}", "type": "text", "referral": {{referral}} } ] }""";

        var message = Assert.Single(Dialog360PayloadParser.Parse(json).Messages);

        Assert.Equal(expected, message.Referral!.Reference); // a referral without any reference is still an ad
    }

    // ---- Truncation never splits an emoji ---------------------------------------------------------------------------------

    [Fact]
    public void TruncationAtAnEmojiBoundary_KeepsEveryStringWellFormed()
    {
        var text = new string('a', Domain.Entities.Message.TextMaxLength - 1) + "😀";
        var name = new string('n', 199) + "😀";
        var reference = new string('r', 499) + "😀";
        var json = $$"""
            { "contacts": [ { "wa_id": "573005550101", "profile": { "name": "{{name}}" } } ],
              "messages": [ { "from": "573005550101", "id": "a", "timestamp": "{{Dialog360Samples.T0}}", "type": "text",
                              "text": { "body": "{{text}}" }, "referral": { "headline": "{{reference}}" } } ] }
            """;

        var message = Assert.Single(Dialog360PayloadParser.Parse(json).Messages);

        Assert.Equal(Domain.Entities.Message.TextMaxLength - 1, message.Text!.Length);
        Assert.Equal(199, message.ContactName!.Length);
        Assert.Equal(499, message.Referral!.Reference!.Length);
        Assert.All(new[] { message.Text, message.ContactName, message.Referral.Reference }, AssertNoLoneSurrogate);
    }

    private static void AssertNoLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                Assert.True(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]), "high surrogate without its pair");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(value[i]), "low surrogate without its pair");
            }
        }
    }
}
