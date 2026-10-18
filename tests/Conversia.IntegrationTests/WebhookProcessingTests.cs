using Conversia.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Conversia.Infrastructure.Webhooks;
using Conversia.Tests.Shared;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// What the background worker makes of stored deliveries: contacts, conversations, messages and opportunities. The worker is driven by
/// hand (<see cref="WebhookEventRunner"/>) so each test decides when events are processed.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class WebhookProcessingTests : IAsyncLifetime
{
    private const long T0 = Dialog360Samples.T0;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WebhookProcessingTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => ClearQueueAsync(_factory.Services);

    public Task DisposeAsync() => Task.CompletedTask;

    private WebhookEventRunner Runner => RunnerOf(_factory.Services);

    private Task<ChannelFixture> NewChannelAsync(string? providerPhoneNumberId = Dialog360Samples.PhoneNumberId) => StartChannelAsync(_factory, _client, providerPhoneNumberId);

    private async Task<Snapshot> ProcessAsync(ChannelFixture f)
    {
        await DrainAsync(Runner);
        return await SnapshotAsync(_factory.Services, f.TenantId);
    }

    // ---- The basic flow ------------------------------------------------------------------------------

    [Fact]
    public async Task AnInboundMessage_CreatesTheContact_TheOpenConversation_TheMessageAndANewOpportunity()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());

        var s = await ProcessAsync(f);

        var contact = Assert.Single(s.Contacts);
        Assert.Equal((Dialog360Samples.CustomerWaId, "Laura Gómez", At(T0)), (contact.WaId, contact.DisplayName, contact.FirstSeenAt));
        var conversation = Assert.Single(s.Conversations);
        Assert.Equal((contact.Id, f.Channel.Id, f.Branch.Id), (conversation.ContactId, conversation.ChannelId, conversation.BranchId));
        Assert.Equal((ConversationStatus.Open, ConversationSource.Organic, (string?)null), (conversation.Status, conversation.Source, conversation.SourceReference));
        Assert.Equal((At(T0), At(T0), At(T0), (DateTime?)null), (conversation.StartedAt, conversation.LastMessageAt, conversation.FirstInboundAt, conversation.FirstResponseAt));
        var message = Assert.Single(s.Messages);
        Assert.Equal((conversation.Id, f.Channel.Id, "wamid.HBgM-IN-1", MessageDirection.Inbound, "text"), (message.ConversationId, message.ChannelId, message.ProviderMessageId, message.Direction, message.Type));
        Assert.Equal(("Hola, quisiera info de depilación láser", At(T0)), (message.Text, message.SentAt));
        var opportunity = Assert.Single(s.Opportunities);
        Assert.Equal((conversation.Id, OpportunityStage.New), (opportunity.ConversationId, opportunity.Stage));
        var processed = Assert.Single(s.Events);
        Assert.Equal(WebhookEventStatus.Processed, processed.Status);
        Assert.NotNull(processed.ProcessedAt);
        Assert.Null(processed.LastError);
    }

    [Fact]
    public async Task TwoMessagesInOneDelivery_BothLandInOneConversation()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.TwoInboundMessages());

        var s = await ProcessAsync(f);

        Assert.Equal(["wamid.HBgM-IN-A", "wamid.HBgM-IN-B"], s.Messages.Select(m => m.ProviderMessageId));
        var conversation = Assert.Single(s.Conversations);
        Assert.Equal((At(T0), At(T0 + 5)), (conversation.StartedAt, conversation.LastMessageAt));
        Assert.Single(s.Contacts);
        Assert.Single(s.Opportunities);
    }

    // ---- First response and ordering ----------------------------------------------------------------------

    [Fact]
    public async Task AnInboundThenAnAdvisorsEcho_SetsTheFirstResponseOnce()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        await DeliverAsync(_client, f, Dialog360Samples.OutboundEcho("wamid.ECHO-1", T0 + 90));
        await DeliverAsync(_client, f, Dialog360Samples.OutboundEcho("wamid.ECHO-2", T0 + 200, text: "Otra respuesta"));

        var s = await ProcessAsync(f);

        var conversation = Assert.Single(s.Conversations);
        Assert.Equal((At(T0), At(T0 + 200), At(T0), At(T0 + 90)), (conversation.StartedAt, conversation.LastMessageAt, conversation.FirstInboundAt, conversation.FirstResponseAt));
        Assert.Equal([MessageDirection.Inbound, MessageDirection.Outbound, MessageDirection.Outbound], s.Messages.Select(m => m.Direction));
        Assert.Single(s.Contacts);
        Assert.Single(s.Opportunities);
    }

    [Fact]
    public async Task AnEchoThatArrivesBeforeTheInboundItAnswers_StillEndsUpWithTheRightFirstResponse()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.OutboundEcho("wamid.ECHO-1", T0 + 60));

        var early = Assert.Single((await ProcessAsync(f)).Conversations);
        Assert.Equal((At(T0 + 60), (DateTime?)null, (DateTime?)null), (early.StartedAt, early.FirstInboundAt, early.FirstResponseAt)); // nobody wrote yet

        await DeliverAsync(_client, f, Dialog360Samples.InboundText(timestamp: T0));
        var s = await ProcessAsync(f);

        var conversation = Assert.Single(s.Conversations); // the same conversation, not a second one
        Assert.Equal(early.Id, conversation.Id);
        Assert.Equal((At(T0), At(T0 + 60), At(T0), At(T0 + 60)), (conversation.StartedAt, conversation.LastMessageAt, conversation.FirstInboundAt, conversation.FirstResponseAt));
        Assert.Equal(At(T0), Assert.Single(s.Contacts).FirstSeenAt);
        Assert.Single(s.Opportunities);
    }

    [Fact]
    public async Task ABusinessMessageSentBeforeTheCustomersFirstOne_IsNotTheFirstResponse()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.OutboundEcho("wamid.ECHO-0", T0 - 600, text: "Promoción de hoy"));
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(timestamp: T0));
        await DeliverAsync(_client, f, Dialog360Samples.OutboundEcho("wamid.ECHO-1", T0 + 30));

        var conversation = Assert.Single((await ProcessAsync(f)).Conversations);

        Assert.Equal((At(T0 - 600), At(T0), At(T0 + 30)), (conversation.StartedAt, conversation.FirstInboundAt, conversation.FirstResponseAt));
    }

    [Fact]
    public async Task AReplyInTheSameSecondAsTheCustomersMessage_IsTheFirstResponse()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.IN", T0));
        await DeliverAsync(_client, f, Dialog360Samples.OutboundEcho("wamid.OUT", T0));

        var conversation = Assert.Single((await ProcessAsync(f)).Conversations);

        Assert.Equal((At(T0), At(T0)), (conversation.FirstInboundAt, conversation.FirstResponseAt));
    }

    [Fact]
    public async Task AReactionOrASystemMessage_NeverSetsTheFirstInboundNorTheFirstResponse()
    {
        using var f = await NewChannelAsync();
        var reaction = $$"""{ "messages": [ { "from": "{{Dialog360Samples.CustomerWaId}}", "id": "wamid.REACT", "timestamp": "{{T0}}", "type": "reaction", "reaction": { "emoji": "👍" } } ] }""";
        await DeliverAsync(_client, f, reaction);

        var s = await ProcessAsync(f);

        Assert.Empty(s.Messages);
        Assert.Empty(s.Conversations);
        Assert.Equal((0, 0), (s.Events[0].SkippedMessageCount, s.Events[0].StatusCount));
    }

    // ---- Messages that cannot be stored are counted, never silent -----------------------------------------------

    [Fact]
    public async Task ADeliveryWhoseCustomerHasNoPhoneNumber_IsCountedAsSkipped_NotSilentlyDropped_AndNeverMergesCustomers()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundFrom("CO.13491208655302741918", "wamid.B1"));
        await DeliverAsync(_client, f, Dialog360Samples.InboundFrom("CO.99999999999999999999", "wamid.B2", T0 + 1));
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.OK", T0 + 2));

        var s = await ProcessAsync(f);

        Assert.Equal([1, 1, 0], s.Events.Select(e => e.SkippedMessageCount!.Value));
        Assert.All(s.Events, e => Assert.Equal(WebhookEventStatus.Processed, e.Status));
        Assert.Equal(Dialog360Samples.CustomerWaId, Assert.Single(s.Contacts).WaId); // the BSUIDs created no contact at all
        Assert.Equal("wamid.OK", Assert.Single(s.Messages).ProviderMessageId);
    }

    [Fact]
    public async Task TheStatusCountOfAReceiptOnlyDelivery_IsPersisted()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.StatusesOnly());

        var webhookEvent = Assert.Single((await ProcessAsync(f)).Events);

        Assert.Equal((0, 2), (webhookEvent.SkippedMessageCount, webhookEvent.StatusCount));
    }

    [Fact]
    public async Task ADeliveryNamingAnotherBusinessNumber_IsCountedAsSkipped()
    {
        using var f = await NewChannelAsync(providerPhoneNumberId: Dialog360Samples.PhoneNumberId);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.FOREIGN", phoneNumberId: "999999"));

        Assert.Equal(1, Assert.Single((await ProcessAsync(f)).Events).SkippedMessageCount);
    }

    // ---- Names, references and boundaries -----------------------------------------------------------------------

    [Fact]
    public async Task AnOlderDeliveryProcessedAfterANewerOne_DoesNotOverwriteTheNewerName()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.NEW", T0 + 3600, name: "Laura Nueva"));
        await ProcessAsync(f);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.OLD", T0, name: "Laura Vieja"));

        var s = await ProcessAsync(f);

        Assert.Equal("Laura Nueva", Assert.Single(s.Contacts).DisplayName);
        Assert.Equal(2, s.Messages.Count);
    }

    [Fact]
    public async Task AReferralWithAnEmptyHeadline_UsesTheUrl_AndALaterReferralFillsAMissingReference()
    {
        using var f = await NewChannelAsync();
        string Ad(string id, long time, string referral) =>
            $$"""{ "messages": [ { "from": "{{Dialog360Samples.CustomerWaId}}", "id": "{{id}}", "timestamp": "{{time}}", "type": "text", "referral": {{referral}} } ] }""";
        await DeliverAsync(_client, f, Ad("wamid.A1", T0, """{ "headline": "", "source_url": "https://fb.me/ad-1" }"""));
        Assert.Equal("https://fb.me/ad-1", Assert.Single((await ProcessAsync(f)).Conversations).SourceReference);

        using var g = await NewChannelAsync();
        await DeliverAsync(_client, g, Ad("wamid.B1", T0, "{ }")); // an ad, but nothing to name it by
        await DeliverAsync(_client, g, Ad("wamid.B2", T0 + 5, """{ "headline": "Promo" }"""));
        var conversation = Assert.Single((await ProcessAsync(g)).Conversations);

        Assert.Equal((ConversationSource.Ads, "Promo"), (conversation.Source, conversation.SourceReference));
    }

    [Fact]
    public async Task TextCutAtAnEmojiBoundary_IsStoredIntact()
    {
        using var f = await NewChannelAsync();
        var text = new string('a', Domain.Entities.Message.TextMaxLength - 1) + "😀";
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(text: text));

        var s = await ProcessAsync(f);

        Assert.Equal(WebhookEventStatus.Processed, Assert.Single(s.Events).Status);
        Assert.Equal(Domain.Entities.Message.TextMaxLength - 1, Assert.Single(s.Messages).Text!.Length);
    }

    // ---- Source -------------------------------------------------------------------------------------

    [Fact]
    public async Task AMessageWithAnAdReferral_StartsAnAdsConversationWithItsReference()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundFromAd());

        var conversation = Assert.Single((await ProcessAsync(f)).Conversations);

        Assert.Equal((ConversationSource.Ads, "Depilación láser 2x1"), (conversation.Source, conversation.SourceReference));
    }

    [Fact]
    public async Task AReferralThatArrivesAfterTheConversationStarted_UpgradesItToAds()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.NO-AD", T0 + 10));
        Assert.Equal(ConversationSource.Organic, Assert.Single((await ProcessAsync(f)).Conversations).Source);

        await DeliverAsync(_client, f, Dialog360Samples.InboundFromAd("wamid.AD", T0));
        var conversation = Assert.Single((await ProcessAsync(f)).Conversations);

        Assert.Equal((ConversationSource.Ads, At(T0)), (conversation.Source, conversation.StartedAt));
    }

    // ---- Things with nothing to store ------------------------------------------------------------------

    [Theory]
    [InlineData("statuses")]
    [InlineData("unknown")]
    [InlineData("malformed entries")]
    public async Task ADeliveryWithNothingToStore_IsProcessedAsIgnored_NotFailed(string kind)
    {
        using var f = await NewChannelAsync();
        var body = kind switch
        {
            "statuses" => Dialog360Samples.StatusesOnly(),
            "unknown" => Dialog360Samples.UnknownEvent(),
            _ => """{ "messages": [ { "from": "x", "id": "a" }, 5, { "text": "no id" } ], "weird": [] }""",
        };
        await DeliverAsync(_client, f, body);

        var s = await ProcessAsync(f);

        var webhookEvent = Assert.Single(s.Events);
        Assert.Equal((WebhookEventStatus.Processed, 1, (string?)null), (webhookEvent.Status, webhookEvent.Attempts, webhookEvent.LastError));
        Assert.Empty(s.Contacts);
        Assert.Empty(s.Conversations);
        Assert.Empty(s.Messages);
        Assert.Empty(s.Opportunities);
    }

    [Fact]
    public async Task MediaWithoutText_IsStoredWithANullText()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundAudio());
        await DeliverAsync(_client, f, Dialog360Samples.InboundImage("wamid.IMG", T0 + 5));

        var s = await ProcessAsync(f);

        Assert.Equal([("audio", (string?)null), ("image", "Así quedó")], s.Messages.Select(m => (m.Type, m.Text)));
    }

    [Fact]
    public async Task TheOlderPayloadShapeWithoutEnvelope_IsProcessedToo()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.LegacyRootShape());

        Assert.Equal("wamid.LEGACY-1", Assert.Single((await ProcessAsync(f)).Messages).ProviderMessageId);
    }

    // ---- Idempotency ----------------------------------------------------------------------------------

    [Fact]
    public async Task AMessageRepeatedInAnotherDelivery_IsStoredOnce()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        await DeliverAsync(_client, f, Dialog360Samples.InboundText() + " "); // another body, the very same message
        await DeliverAsync(_client, f, Dialog360Samples.TwoInboundMessages(T0 + 1000)); // new ids

        var s = await ProcessAsync(f);

        Assert.Equal(3, s.Messages.Count);
        Assert.Equal(3, s.Events.Count);
        Assert.All(s.Events, e => Assert.Equal(WebhookEventStatus.Processed, e.Status));
        Assert.Single(s.Contacts);
        Assert.Single(s.Conversations);
        Assert.Single(s.Opportunities);
    }

    [Fact]
    public async Task AProcessedEventIsNeverProcessedAgain()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        await ProcessAsync(f);

        Assert.False(await Runner.ProcessNextAsync());
        Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Messages);
    }

    // ---- Contacts and conversations ---------------------------------------------------------------------------

    [Fact]
    public async Task TheSameCustomerWritingAgain_ReusesTheContactAndTheOpenConversation_AndKeepsTheLatestName()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.1", T0, name: "Laura"));
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.2", T0 + 3600, name: "Laura Gómez"));

        var s = await ProcessAsync(f);

        Assert.Equal("Laura Gómez", Assert.Single(s.Contacts).DisplayName);
        Assert.Equal(2, s.Messages.Count);
        Assert.Single(s.Conversations);
        Assert.Single(s.Opportunities);
    }

    [Fact]
    public async Task DifferentCustomers_GetTheirOwnContactConversationAndOpportunity()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.1", T0, waId: "573005550101"));
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.2", T0 + 1, waId: "573005550102", name: "Mario"));

        var s = await ProcessAsync(f);

        Assert.Equal(2, s.Contacts.Count);
        Assert.Equal(2, s.Conversations.Count);
        Assert.Equal(2, s.Opportunities.Select(o => o.ConversationId).Distinct().Count());
    }

    [Fact]
    public async Task TheSameCustomerOnTwoChannelsOfTheTenant_IsOneContactWithOneConversationPerChannel()
    {
        using var f = await NewChannelAsync(providerPhoneNumberId: "111");
        var second = await AddChannelAsync(f, "222");
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.A", phoneNumberId: "111"));
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.B", phoneNumberId: "222"), path: second.WebhookPath, secret: second.WebhookSecret);

        var s = await ProcessAsync(f);

        Assert.Single(s.Contacts);
        Assert.Equal(new[] { f.Channel.Id, second.Id }.Order(), s.Conversations.Select(c => c.ChannelId).Order());
        Assert.Equal(2, s.Opportunities.Count);
    }

    [Fact]
    public async Task ADeliveryNamingAnotherBusinessNumber_IsIgnored_WhileTheChannelsOwnNumberIsStored()
    {
        using var f = await NewChannelAsync(providerPhoneNumberId: Dialog360Samples.PhoneNumberId);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.FOREIGN", phoneNumberId: "999999"));
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.OWN", T0 + 1));

        var s = await ProcessAsync(f);

        Assert.Equal("wamid.OWN", Assert.Single(s.Messages).ProviderMessageId);
        Assert.All(s.Events, e => Assert.Equal(WebhookEventStatus.Processed, e.Status));
    }

    [Fact]
    public async Task AChannelWithoutAProviderNumberId_AcceptsWhateverNumberTheDeliveryNames()
    {
        using var f = await NewChannelAsync(providerPhoneNumberId: null);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(phoneNumberId: "anything"));

        Assert.Single((await ProcessAsync(f)).Messages);
    }

    // ---- A closed conversation takes no more messages -------------------------------------------------------------

    [Fact]
    public async Task AMessageAfterTheConversationWasClosed_OpensANewConversation_WithItsOwnOpportunity()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.BEFORE", T0));
        await DeliverAsync(_client, f, Dialog360Samples.OutboundEcho("wamid.ECHO", T0 + 60));
        var before = Assert.Single((await ProcessAsync(f)).Conversations);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Conversia.Infrastructure.Persistence.ApplicationDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE \"Conversations\" SET \"Status\" = 'Closed', \"ClosedAt\" = {before.LastMessageAt} WHERE \"Id\" = {before.Id}");
        }

        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.AFTER", T0 + 86_400, text: "Hola, sigo interesada"));
        var s = await ProcessAsync(f);

        Assert.Equal(2, s.Conversations.Count);
        var closed = s.Conversations.Single(c => c.Id == before.Id);
        var reopened = s.Conversations.Single(c => c.Id != before.Id);
        Assert.Equal(ConversationStatus.Closed, closed.Status);
        Assert.Equal((ConversationStatus.Open, At(T0 + 86_400), At(T0 + 86_400), (DateTime?)null), (reopened.Status, reopened.StartedAt, reopened.FirstInboundAt, reopened.FirstResponseAt));
        Assert.Equal(["wamid.BEFORE", "wamid.ECHO"], s.Messages.Where(m => m.ConversationId == closed.Id).Select(m => m.ProviderMessageId));
        Assert.Equal(["wamid.AFTER"], s.Messages.Where(m => m.ConversationId == reopened.Id).Select(m => m.ProviderMessageId));
        Assert.Single(s.Contacts); // the same customer
        Assert.Equal(2, s.Opportunities.Count);
        Assert.All(s.Opportunities, o => Assert.Equal(OpportunityStage.New, o.Stage));
        Assert.Equal(closed.LastMessageAt, At(T0 + 60)); // the closed one did not take the late message
    }

    // ---- Tenant isolation --------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoTenantsReceivingFromTheSameCustomer_NeverShareOrLeakData()
    {
        using var a = await NewChannelAsync();
        using var b = await NewChannelAsync();
        await DeliverAsync(_client, a, Dialog360Samples.InboundText("wamid.SAME", T0, name: "Laura de A"));
        await DeliverAsync(_client, b, Dialog360Samples.InboundText("wamid.SAME", T0, name: "Laura de B"));
        await DeliverAsync(_client, a, Dialog360Samples.OutboundEcho("wamid.ECHO-A", T0 + 30));

        var sa = await ProcessAsync(a);
        var sb = await SnapshotAsync(_factory.Services, b.TenantId);

        Assert.Equal("Laura de A", Assert.Single(sa.Contacts).DisplayName);
        Assert.Equal("Laura de B", Assert.Single(sb.Contacts).DisplayName);
        Assert.Equal(2, sa.Messages.Count);
        Assert.Single(sb.Messages); // A's echo never reached B
        Assert.All(sa.Messages, m => Assert.Equal(a.Channel.Id, m.ChannelId));
        Assert.All(sb.Messages, m => Assert.Equal(b.Channel.Id, m.ChannelId));
        Assert.Equal(a.TenantId, Assert.Single(sa.Conversations).TenantId);
        Assert.Equal(b.TenantId, Assert.Single(sb.Conversations).TenantId);
        Assert.NotEqual(sa.Contacts[0].Id, sb.Contacts[0].Id);
        Assert.All(sb.Events, e => Assert.Equal(WebhookEventStatus.Processed, e.Status));
    }
}
