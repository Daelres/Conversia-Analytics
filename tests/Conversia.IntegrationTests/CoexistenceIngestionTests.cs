using System.Net.Http.Json;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Conversia.Infrastructure.Webhooks;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// A number shared by the WhatsApp Business app and the API (docs/WEBHOOKS.md, "Coexistence"): echoes, the shared history behind its opt-in,
/// the app's address book never stored, and the account notifications that flag the channel. Doc-shaped payloads (tests/Shared/Dialog360Samples.cs).
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class CoexistenceIngestionTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CoexistenceIngestionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => ClearQueueAsync(_factory.Services);

    public Task DisposeAsync() => Task.CompletedTask;

    private WebhookEventRunner Runner => RunnerOf(_factory.Services);

    private async Task<Snapshot> ProcessAsync(ChannelFixture f)
    {
        await DrainAsync(Runner);
        return await SnapshotAsync(_factory.Services, f.TenantId);
    }

    private async Task EnableHistoryAsync(ChannelFixture f, bool analyzeWithAi = false)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var channel = await db.WhatsAppChannels.IgnoreQueryFilters().SingleAsync(c => c.Id == f.Channel.Id);
        channel.EnableHistoryImport(f.Tenant.Owner.UserId, DateTime.UtcNow, analyzeWithAi);
        await db.SaveChangesAsync();
    }

    private async Task<Domain.Entities.WhatsAppChannel> ChannelAsync(ChannelFixture f)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.WhatsAppChannels.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == f.Channel.Id);
    }

    [Fact]
    public async Task ADocShapedImageEcho_IsStoredAsAnOutboundPhoneAppMessage_WaitingForItsDownload()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        await DeliverAsync(_client, f, Dialog360Samples.EchoImageDocShaped());

        var s = await ProcessAsync(f);

        var echo = s.Messages.Single(m => m.ProviderMessageId == "wamid.HBgM-ECHO-IMG-1");
        Assert.Equal((MessageDirection.Outbound, MessageSender.PhoneApp, "image", MediaStatus.Pending), (echo.Direction, echo.Sender, echo.Type, echo.MediaStatus!.Value));
        Assert.Single(s.Conversations);
    }

    [Fact]
    public async Task AHistoryChunk_WithTheImportOff_StoresNothing_AndTheDeliveryIsProcessed()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await DeliverAsync(_client, f, Dialog360Samples.HistoryChunk());

        var s = await ProcessAsync(f);

        Assert.Empty(s.Messages);
        Assert.Empty(s.Contacts);
        Assert.Equal(WebhookEventStatus.Processed, Assert.Single(s.Events).Status);
    }

    [Fact]
    public async Task AHistoryChunk_WithTheImportOn_BuildsOneClosedImportedConversation_ReadByTheRulesOnly()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await EnableHistoryAsync(f);
        await DeliverAsync(_client, f, Dialog360Samples.HistoryChunk());

        var s = await ProcessAsync(f);

        Assert.Equal(3, s.Messages.Count);
        Assert.All(s.Messages, m => Assert.True(m.IsImported));
        Assert.Equal(MessageSender.PhoneApp, s.Messages.Single(m => m.ProviderMessageId == "wamid.HIST-OUT-1").Sender);
        var conversation = Assert.Single(s.Conversations);
        Assert.True(conversation.IsImported);
        Assert.Equal(ConversationStatus.Closed, conversation.Status);
        Assert.Equal(conversation.LastMessageAt, conversation.ClosedAt);
        Assert.True(conversation.ClassificationDirty);
        Assert.False(conversation.LlmPending);
    }

    [Fact]
    public async Task WithTheAiOptIn_TheImportedConversationIsLeftForTheAiToo()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await EnableHistoryAsync(f, analyzeWithAi: true);
        await DeliverAsync(_client, f, Dialog360Samples.HistoryChunk());

        var s = await ProcessAsync(f);

        Assert.True(Assert.Single(s.Conversations).LlmPending);
    }

    [Fact]
    public async Task ALaterChunkJoinsTheImportedConversation_AndALiveMessageOpensItsOwn()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await EnableHistoryAsync(f);
        await DeliverAsync(_client, f, Dialog360Samples.HistoryChunk());
        await DrainAsync(Runner);
        await DeliverAsync(_client, f, Dialog360Samples.HistoryChunk(phase: 2, chunkOrder: 2, progress: 100, timestamp: Dialog360Samples.T0 - 86_400 * 100)
            .Replace("wamid.HIST-", "wamid.HIST2-", StringComparison.Ordinal));
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());

        var s = await ProcessAsync(f);

        var imported = Assert.Single(s.Conversations, c => c.IsImported);
        Assert.Equal(6, s.Messages.Count(m => m.ConversationId == imported.Id));
        var live = Assert.Single(s.Conversations, c => !c.IsImported);
        Assert.Equal(ConversationStatus.Open, live.Status);
        Assert.False(s.Messages.Single(m => m.ConversationId == live.Id).IsImported);
    }

    [Fact]
    public async Task TheMediaFollowUpOfAPlaceholder_CompletesIt_AndQueuesItsDownload()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await EnableHistoryAsync(f);
        await DeliverAsync(_client, f, Dialog360Samples.HistoryChunk());
        await DrainAsync(Runner);
        await DeliverAsync(_client, f, Dialog360Samples.HistoryMediaFollowUp(id: "wamid.HIST-OUT-2"));

        var s = await ProcessAsync(f);

        var completed = s.Messages.Single(m => m.ProviderMessageId == "wamid.HIST-OUT-2");
        Assert.Equal(("image", "24230790383178699", MediaStatus.Pending), (completed.Type, completed.MediaId, completed.MediaStatus!.Value));
        Assert.Equal(3, s.Messages.Count);
    }

    [Fact]
    public async Task TheAppsAddressBook_IsNeverStored()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await DeliverAsync(_client, f, Dialog360Samples.AppStateSync());

        var s = await ProcessAsync(f);

        Assert.Empty(s.Contacts);
        Assert.Empty(s.Messages);
        Assert.Equal(WebhookEventStatus.Processed, Assert.Single(s.Events).Status);
    }

    [Fact]
    public async Task PartnerRemoved_FlagsTheChannel_AndReconnected_ClearsIt()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await DeliverAsync(_client, f, Dialog360Samples.AccountUpdate("PARTNER_REMOVED", "PRIMARY_INACTIVITY"));
        await DrainAsync(Runner);

        var flagged = await ChannelAsync(f);
        Assert.True(flagged.IsProviderDisconnected);
        Assert.Equal("PRIMARY_INACTIVITY", flagged.ProviderDisconnectionReason);

        await DeliverAsync(_client, f, Dialog360Samples.AccountUpdate("ACCOUNT_RECONNECTED", time: Dialog360Samples.T0 + 60));
        await DrainAsync(Runner);

        Assert.False((await ChannelAsync(f)).IsProviderDisconnected);
    }

    private async Task<List<string>> RawPayloadsAsync(ChannelFixture f)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == f.TenantId).Select(e => e.RawPayload).ToListAsync();
    }

    [Fact]
    public async Task TheAppsAddressBook_IsRedactedBeforeTheRawDeliveryIsKept()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await DeliverAsync(_client, f, Dialog360Samples.AppStateSync());

        var raw = Assert.Single(await RawPayloadsAsync(f));

        Assert.DoesNotContain("Laura", raw);
        Assert.DoesNotContain("573005550102", raw);
        Assert.DoesNotContain(Dialog360Samples.CustomerWaId, raw);
        Assert.DoesNotContain("CO.99", raw);
    }

    [Fact]
    public async Task AHistory_WithTheImportOff_IsNotKeptInTheRawDelivery()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await DeliverAsync(_client, f, Dialog360Samples.HistoryChunk());

        var raw = Assert.Single(await RawPayloadsAsync(f));

        Assert.DoesNotContain("Gracias", raw);
        Assert.DoesNotContain("wamid.HIST", raw);
        Assert.DoesNotContain(Dialog360Samples.CustomerWaId, raw);
    }

    [Fact]
    public async Task AHistory_WithTheImportOn_IsKeptForProcessing()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await EnableHistoryAsync(f);
        await DeliverAsync(_client, f, Dialog360Samples.HistoryChunk());

        Assert.Contains("wamid.HIST-IN-1", Assert.Single(await RawPayloadsAsync(f)));
    }

    private static string LargeHistory(int messages)
    {
        var threads = string.Join(",", Enumerable.Range(0, messages / 50).Select(t =>
        {
            var waId = $"5730077{t:D5}";
            var items = string.Join(",", Enumerable.Range(0, 50).Select(i =>
                $$$"""{"from":"{{{(i % 2 == 0 ? waId : Dialog360Samples.BusinessNumber)}}}","id":"wamid.BIG-{{{t}}}-{{{i}}}","timestamp":"{{{Dialog360Samples.T0 - 86_400 * 20 + i}}}","type":"text","text":{"body":"m{{{i}}}"}}"""));
            return $$"""{"id":"{{waId}}","context":{"wa_id":"{{waId}}"},"messages":[{{items}}]}""";
        }));
        return $$$"""
            {"object":"whatsapp_business_account","entry":[{"id":"1","changes":[{"field":"history","value":{"messaging_product":"whatsapp",
            "metadata":{"display_phone_number":"{{{Dialog360Samples.BusinessNumber}}}","phone_number_id":"{{{Dialog360Samples.PhoneNumberId}}}"},
            "history":[{"metadata":{"phase":1,"chunk_order":1,"progress":50},"threads":[{{{threads}}}]}]}}]}]}
            """;
    }

    [Fact]
    public async Task AHistoryOf5000Messages_IsSplitIntoBoundedDeliveries_ProcessedQuickly_AndIdempotent()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await EnableHistoryAsync(f);
        var body = LargeHistory(5000);

        await DeliverAsync(_client, f, body);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await DrainAsync(Runner);
        watch.Stop();
        await DeliverAsync(_client, f, body);
        await DrainAsync(Runner);

        var s = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.Equal(5000, s.Messages.Count);
        Assert.Equal(100, s.Conversations.Count(c => c.IsImported));
        Assert.Equal(10, s.Events.Count);
        Assert.All(s.Events, e => Assert.Equal(WebhookEventStatus.Processed, e.Status));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task ImportedConversations_AreOutsideTheMetrics()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await EnableHistoryAsync(f);
        await DeliverAsync(_client, f, Dialog360Samples.HistoryChunk()); // 30 days before T0 (2026-09-18)
        await ProcessAsync(f);
        const string period = "from=2026-09-01&to=2026-10-15";

        var volume = await f.Tenant.Owner.Client.GetFromJsonAsync<Application.DTOs.Metrics.VolumeResponse>($"/api/metrics/volume?{period}");
        var summary = await f.Tenant.Owner.Client.GetFromJsonAsync<Application.DTOs.Metrics.SummaryResponse>($"/api/metrics/summary?{period}");

        Assert.Equal((0, 0, 0), (volume!.Conversations, volume.InboundMessages, volume.OutboundMessages));
        Assert.Equal(0, summary!.Conversion.Total);
    }
}
