using Conversia.Application.Ingestion;
using Conversia.Domain.Enums;
using Conversia.Tests.Shared;

namespace Conversia.Application.UnitTests.Ingestion;

/// <summary>
/// Coexistence deliveries (WhatsApp Business app and API on the same number), against payloads shaped after Meta's webhook references for
/// <c>smb_message_echoes</c>, <c>history</c>, <c>smb_app_state_sync</c> and <c>account_update</c> (docs/WEBHOOKS.md, "Coexistence").
/// </summary>
public class Dialog360CoexistenceParserTests
{
    [Fact]
    public void ADocShapedImageEcho_IsAnOutboundImageToTheCustomer_WithItsMediaId()
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.EchoImageDocShaped());

        var message = Assert.Single(parsed.Messages);
        Assert.Equal(MessageDirection.Outbound, message.Direction);
        Assert.Equal(Dialog360Samples.CustomerWaId, message.ContactWaId);
        Assert.Equal("image", message.Type);
        Assert.Equal("Así queda la zona", message.Text);
        Assert.Equal("24230790383178626", message.Media!.ProviderMediaId);
        Assert.Equal("image/jpeg", message.Media.MimeType);
        Assert.False(message.Imported);
        Assert.Null(message.ContactName); // an echo never names the customer
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("edit")]
    public void ARevokeOrEditEcho_IsIgnored_NeverStoredAsAMessage(string type)
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.EchoRevokeOrEdit(type));

        Assert.Empty(parsed.Messages);
        Assert.Equal(1, parsed.IgnoredCount);
        Assert.Equal(0, parsed.SkippedCount);
    }

    [Fact]
    public void AHistoryChunk_YieldsImportedMessages_WithTheDirectionFromTheSender()
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.HistoryChunk());

        Assert.Equal(3, parsed.Messages.Count);
        Assert.All(parsed.Messages, m => Assert.True(m.Imported));
        Assert.All(parsed.Messages, m => Assert.Equal(Dialog360Samples.CustomerWaId, m.ContactWaId));
        var byId = parsed.Messages.ToDictionary(m => m.ProviderMessageId);
        Assert.Equal(MessageDirection.Outbound, byId["wamid.HIST-OUT-1"].Direction);
        Assert.Equal("Aquí tienes la información que pediste", byId["wamid.HIST-OUT-1"].Text);
        Assert.Equal("media_placeholder", byId["wamid.HIST-OUT-2"].Type);
        Assert.Null(byId["wamid.HIST-OUT-2"].Media);
        Assert.Equal(MessageDirection.Inbound, byId["wamid.HIST-IN-1"].Direction);
        Assert.Null(byId["wamid.HIST-IN-1"].Referral);

        var chunk = Assert.Single(parsed.HistoryChunks);
        Assert.Equal((1, 1, 55, false), (chunk.Phase, chunk.ChunkOrder, chunk.Progress, chunk.Declined));
    }

    [Fact]
    public void AHistoryThreadWithoutAPhoneNumber_IsSkippedAndCounted()
    {
        var json = Dialog360Samples.HistoryChunk(threadWaId: "CO.13491208655302741918");

        var parsed = Dialog360PayloadParser.Parse(json);

        Assert.Empty(parsed.Messages);
        Assert.Equal(3, parsed.SkippedCount);
    }

    [Fact]
    public void TheMediaFollowUpOfAHistoryPlaceholder_IsAnImportedMediaMessage()
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.HistoryMediaFollowUp());

        var message = Assert.Single(parsed.Messages);
        Assert.True(message.Imported);
        Assert.Equal(MessageDirection.Inbound, message.Direction);
        Assert.Equal("24230790383178699", message.Media!.ProviderMediaId);
    }

    [Fact]
    public void ADeclinedHistory_IsAChunkMarkedDeclined_WithNoMessages()
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.HistoryDeclined());

        Assert.Empty(parsed.Messages);
        Assert.True(Assert.Single(parsed.HistoryChunks).Declined);
    }

    [Fact]
    public void TheAppStateSync_IsCountedAndNothingOfTheAddressBookIsKept()
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.AppStateSync());

        Assert.Empty(parsed.Messages);
        Assert.Equal(2, parsed.AppStateSyncIgnored);
        Assert.Equal(0, parsed.SkippedCount);
    }

    [Theory]
    [InlineData("PARTNER_REMOVED", "PRIMARY_INACTIVITY", "PRIMARY_INACTIVITY")]
    [InlineData("PARTNER_REMOVED", null, null)]
    [InlineData("ACCOUNT_OFFBOARDED", null, null)]
    [InlineData("ACCOUNT_RECONNECTED", null, null)]
    public void ACoexistenceAccountUpdate_IsReadWithItsReasonAndTime(string accountEvent, string? reason, string? expectedReason)
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.AccountUpdate(accountEvent, reason));

        var update = Assert.Single(parsed.AccountUpdates);
        Assert.Equal(accountEvent, update.Event);
        Assert.Equal(expectedReason, update.Reason);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Dialog360Samples.T0).UtcDateTime, update.OccurredAt);
        Assert.Empty(parsed.Messages);
    }

    [Fact]
    public void AnAccountUpdateUnrelatedToCoexistence_IsNotReported()
        => Assert.Empty(Dialog360PayloadParser.Parse(Dialog360Samples.UnknownEvent()).AccountUpdates);

    [Fact]
    public void AnOddReason_IsDropped_TheEventStays()
    {
        var parsed = Dialog360PayloadParser.Parse(Dialog360Samples.AccountUpdate("PARTNER_REMOVED", "<script>alert(1)</script>"));

        Assert.Null(Assert.Single(parsed.AccountUpdates).Reason);
    }

    [Fact]
    public void ALiveMessagesDelivery_IsNeverMarkedImported()
        => Assert.False(Assert.Single(Dialog360PayloadParser.Parse(Dialog360Samples.InboundImage()).Messages).Imported);
}
