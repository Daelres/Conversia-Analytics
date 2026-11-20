using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Inbox;
using Conversia.Application.DTOs.Templates;
using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Stage 3a against PostgreSQL and a stand-in provider (docs/MESSAGING.md): the customer service window, templates (sync, send, status
/// webhook), the inbox and its delta, read receipts and the typing indicator. Nothing here reaches 360dialog.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class InboxAndTemplatesApiTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly MessagingHost _host;

    public InboxAndTemplatesApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _host = MessagingHostOf(factory);
    }

    private static readonly TemplateSnapshot Reminder =
        new("recordatorio_cita", "es", TemplateCategory.Utility, TemplateStatus.Approved, "Hola {{1}}, tu cita es el {{2}}.", true);

    private static readonly TemplateSnapshot Promo =
        new("promo_mes", "es", TemplateCategory.Marketing, TemplateStatus.Approved, "Promoción para ti.", true);

    private async Task<(ChannelFixture Fixture, HttpClient Owner)> StartAsync()
    {
        var f = await StartChannelAsync(_factory, _client);
        var owner = _host.ClientOf(f.Tenant.Owner);
        await SetCredentialsAsync(owner, f.Channel.Id);
        return (f, owner);
    }

    private async Task SyncAsync(HttpClient owner, Guid channelId, params TemplateSnapshot[] templates)
    {
        _host.Sender.ListTemplates(channelId, new TemplateListResult(WhatsAppSendStatus.Sent, templates));
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/channels/{channelId}/templates/sync", null)).StatusCode);
    }

    private static Task<HttpResponseMessage> SendTemplateAsync(HttpClient client, Guid conversationId, string name, params string[] values)
        => client.PostAsJsonAsync($"/api/conversations/{conversationId}/template-messages", new { templateName = name, language = "es", parameters = values });

    private async Task SetSandboxModeAsync(Guid tenantId, bool enabled)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlAsync($"""UPDATE "Tenants" SET "SandboxMode" = {enabled} WHERE "Id" = {tenantId}""");
    }

    // ---- The customer service window ----------------------------------------------------------------------

    [Fact]
    public async Task FreeText_MoreThan24HoursAfterTheCustomer_Is409WithItsCode_AndNeverReachesTheProvider()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddHours(-25), [In(0)]);

        var response = await SendMessageAsync(owner, seeded.ConversationId, "Hola");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("customer_service_window_closed", (await ReadProblemCodeAsync(response)).Code);
        Assert.Empty(_host.Sender.SentBy(f.Channel.Id));

        var detail = await owner.GetFromJsonAsync<ConversationDetailResponse>($"/api/conversations/{seeded.ConversationId}");
        Assert.False(detail!.WindowOpen);
        Assert.NotNull(detail.WindowClosesAt);
        Assert.True(detail.AwaitingReply);
    }

    [Fact]
    public async Task TheDetail_SaysTheWindowIsOpen_AndWhenItCloses()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        var start = DateTime.UtcNow.AddHours(-23).AddMinutes(-30);
        var seeded = await SeedConversationAsync(_factory.Services, f, start, [In(0), Out(60)]);

        var detail = await owner.GetFromJsonAsync<ConversationDetailResponse>($"/api/conversations/{seeded.ConversationId}");

        Assert.True(detail!.WindowOpen);
        Assert.True(detail.WindowClosingSoon);
        Assert.False(detail.AwaitingReply);
        Assert.Equal(start.AddHours(24), detail.WindowClosesAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    // ---- Templates ----------------------------------------------------------------------

    [Fact]
    public async Task Sync_StoresTheProvidersList_AndASecondSyncRefreshesAndRemoves()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;

        await SyncAsync(owner, f.Channel.Id, Reminder, Promo);
        var first = await owner.GetFromJsonAsync<ChannelTemplatesResponse>($"/api/channels/{f.Channel.Id}/templates");
        Assert.Equal(["promo_mes", "recordatorio_cita"], first!.Templates.Select(t => t.Name));
        Assert.Equal(["1", "2"], first.Templates[1].ParameterNames);

        _host.Sender.ListTemplates(f.Channel.Id, new TemplateListResult(WhatsAppSendStatus.Sent, [Reminder with { Status = TemplateStatus.Paused }]));
        var second = await (await owner.PostAsync($"/api/channels/{f.Channel.Id}/templates/sync", null)).Content.ReadFromJsonAsync<TemplateSyncResponse>();
        Assert.Equal((0, 1, 1), (second!.Added, second.Refreshed, second.Removed));

        var after = await owner.GetFromJsonAsync<ChannelTemplatesResponse>($"/api/channels/{f.Channel.Id}/templates");
        Assert.Equal("Paused", Assert.Single(after!.Templates).Status);
    }

    [Fact]
    public async Task Sync_ARefusedKey_Is409_AndKeepsTheStoredCopy()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        await SyncAsync(owner, f.Channel.Id, Reminder);
        _host.Sender.ListTemplates(f.Channel.Id, TemplateListResult.Failed(WhatsAppSendStatus.InvalidCredentials));

        var response = await owner.PostAsync($"/api/channels/{f.Channel.Id}/templates/sync", null);

        Assert.Equal("provider_credentials_invalid", (await ReadProblemCodeAsync(response)).Code);
        Assert.Single((await owner.GetFromJsonAsync<ChannelTemplatesResponse>($"/api/channels/{f.Channel.Id}/templates"))!.Templates);
    }

    [Fact]
    public async Task Templates_OfAnotherCompany_AreNotFound()
    {
        var (f, _) = await StartAsync();
        using var __ = f;
        var (other, otherOwner) = await StartAsync();
        using var ___ = other;

        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.GetAsync($"/api/channels/{f.Channel.Id}/templates")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.PostAsync($"/api/channels/{f.Channel.Id}/templates/sync", null)).StatusCode);
    }

    [Fact]
    public async Task ATemplate_OutsideTheWindow_IsSentAndRecordedWithItsNameCategoryAndAuthor()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        await SyncAsync(owner, f.Channel.Id, Reminder);
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddDays(-3), [In(0)]);

        var response = await SendTemplateAsync(owner, seeded.ConversationId, "recordatorio_cita", "Laura", "lunes");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sent = Assert.Single(_host.Sender.TemplatesSentBy(f.Channel.Id));
        Assert.Equal(("recordatorio_cita", "es"), (sent.Name, sent.Language));
        Assert.Equal(["Laura", "lunes"], sent.Parameters.Select(p => p.Value));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Messages.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.ConversationId == seeded.ConversationId && m.Direction == MessageDirection.Outbound);
        Assert.Equal(("template", "Hola Laura, tu cita es el lunes.", "recordatorio_cita", (TemplateCategory?)TemplateCategory.Utility),
            (stored.Type, stored.Text, stored.TemplateName, stored.TemplateCategory));
        Assert.Equal((MessageSender.Conversia, (Guid?)f.Tenant.Owner.UserId), (stored.Sender, stored.SentByUserId));

        var detail = await owner.GetFromJsonAsync<ConversationDetailResponse>($"/api/conversations/{seeded.ConversationId}");
        Assert.Equal("Utility", detail!.Messages[^1].TemplateCategory);
    }

    [Fact]
    public async Task ATemplate_WithWrongValues_Is400_AndAnUnknownOne_Is409()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        await SyncAsync(owner, f.Channel.Id, Reminder);
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddDays(-3), [In(0)]);

        Assert.Equal(HttpStatusCode.BadRequest, (await SendTemplateAsync(owner, seeded.ConversationId, "recordatorio_cita", "solo uno")).StatusCode);
        var unknown = await SendTemplateAsync(owner, seeded.ConversationId, "no_existe", "x");
        Assert.Equal("template_unavailable", (await ReadProblemCodeAsync(unknown)).Code);
        Assert.Empty(_host.Sender.TemplatesSentBy(f.Channel.Id));
    }

    [Fact]
    public async Task TheTemplateStatusWebhook_PausesTheStoredTemplate_SoItIsNoLongerSent()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        await SyncAsync(owner, f.Channel.Id, Reminder);
        await ClearQueueAsync(_factory.Services);

        await DeliverAsync(_client, f, Dialog360Samples.TemplateStatusUpdate("PAUSED", "recordatorio_cita", "es"));
        await DrainAsync(RunnerOf(_factory.Services));

        var templates = await owner.GetFromJsonAsync<ChannelTemplatesResponse>($"/api/channels/{f.Channel.Id}/templates");
        Assert.Equal("Paused", Assert.Single(templates!.Templates).Status);
        Assert.False(templates.Templates[0].CanBeSent);

        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)]);
        var response = await SendTemplateAsync(owner, seeded.ConversationId, "recordatorio_cita", "Laura", "lunes");
        Assert.Equal("template_unavailable", (await ReadProblemCodeAsync(response)).Code);
    }

    [Fact]
    public async Task TemplateNotifications_DeliveredOutOfOrder_KeepTheLatestStatus()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        await SyncAsync(owner, f.Channel.Id, Reminder);
        await ClearQueueAsync(_factory.Services);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await DeliverAsync(_client, f, Dialog360Samples.TemplateStatusUpdate("PAUSED", "recordatorio_cita", "es", time: now));
        await DrainAsync(RunnerOf(_factory.Services));
        await DeliverAsync(_client, f, Dialog360Samples.TemplateStatusUpdate("APPROVED", "recordatorio_cita", "es", time: now - 600)); // older, arrives later
        await DrainAsync(RunnerOf(_factory.Services));

        var templates = await owner.GetFromJsonAsync<ChannelTemplatesResponse>($"/api/channels/{f.Channel.Id}/templates");
        Assert.Equal("Paused", Assert.Single(templates!.Templates).Status);
    }

    [Fact]
    public async Task InSandboxMode_TheTemplatesAreTheFixedList_AndOneIsSentWithoutAKey()
    {
        var f = await StartChannelAsync(_factory, _client);
        using var _ = f;
        var owner = _host.ClientOf(f.Tenant.Owner);
        await SetSandboxModeAsync(f.TenantId, true);
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddDays(-2), [In(0)], sandbox: true);

        var templates = await owner.GetFromJsonAsync<ChannelTemplatesResponse>($"/api/conversations/{seeded.ConversationId}/templates");
        Assert.True(templates!.Sandbox);
        var response = await SendTemplateAsync(owner, seeded.ConversationId, "conversia_retomar_conversacion", "Laura");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(_host.Sender.TemplatesSentBy(f.Channel.Id)); // the internal sender answered, not the provider's stand-in
    }

    [Fact]
    public async Task SuppressingTheContact_DeletesItsTemplateMessagesToo()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        await SyncAsync(owner, f.Channel.Id, Reminder);
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddDays(-3), [In(0)]);
        Assert.Equal(HttpStatusCode.Created, (await SendTemplateAsync(owner, seeded.ConversationId, "recordatorio_cita", "Laura", "lunes")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/contacts/{seeded.ContactId}/suppression", null)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Messages.IgnoreQueryFilters().AnyAsync(m => m.TenantId == f.TenantId && m.TemplateName != null));
        Assert.True(await db.MessageTemplates.IgnoreQueryFilters().AnyAsync(t => t.TenantId == f.TenantId)); // the business's templates stay
    }

    // ---- The inbox ----------------------------------------------------------------------

    [Fact]
    public async Task Inbox_AwaitingReplyFirst_LongestWaitingFirst_ThenTheRest_WithoutClosedOrSandboxOnes()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        var now = DateTime.UtcNow;
        var answered = await SeedConversationAsync(_factory.Services, f, now.AddMinutes(-3), [In(0), Out(60)]);
        var recent = await SeedConversationAsync(_factory.Services, f, now.AddMinutes(-10), [In(0)]);
        var longest = await SeedConversationAsync(_factory.Services, f, now.AddHours(-2), [In(0)]);
        await SeedConversationAsync(_factory.Services, f, now.AddMinutes(-1), [In(0)], closed: true);
        await SeedConversationAsync(_factory.Services, f, now.AddMinutes(-1), [In(0)], sandbox: true);

        var inbox = await owner.GetFromJsonAsync<InboxResponse>("/api/inbox");

        Assert.Equal([longest.ConversationId, recent.ConversationId, answered.ConversationId], inbox!.Items.Select(i => i.Id));
        Assert.Equal([true, true, false], inbox.Items.Select(i => i.AwaitingReply));
        Assert.All(inbox.Items, i => Assert.True(i.WindowOpen));

        var awaiting = await owner.GetFromJsonAsync<InboxResponse>("/api/inbox?awaitingReply=true");
        Assert.Equal([longest.ConversationId, recent.ConversationId], awaiting!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Inbox_Filters_ByBranchUnassignedAndMine()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        var advisor = await CreateAdvisorAsync(owner, f.Branch.Id, userId: f.Tenant.Owner.UserId);
        var mine = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)], advisorId: advisor.Id);
        var free = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-4), [In(0)]);

        Assert.Equal([mine.ConversationId], (await owner.GetFromJsonAsync<InboxResponse>("/api/inbox?mine=true"))!.Items.Select(i => i.Id));
        Assert.Equal([free.ConversationId], (await owner.GetFromJsonAsync<InboxResponse>("/api/inbox?unassigned=true"))!.Items.Select(i => i.Id));
        Assert.Empty((await owner.GetFromJsonAsync<InboxResponse>($"/api/inbox?branchId={Guid.NewGuid()}"))!.Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/inbox?mine=true&unassigned=true")).StatusCode);

        using var member = await f.Tenant.AddMemberAsync("inboxmember", Permission.ConversationsView);
        var memberInbox = await _host.ClientOf(member).GetFromJsonAsync<InboxResponse>("/api/inbox?mine=true");
        Assert.True(memberInbox!.NoAdvisorLinked);
        Assert.Empty(memberInbox.Items);
    }

    [Fact]
    public async Task Inbox_NeedsConversationsView_AndShowsOnlyTheCallersCompany()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        var (other, otherOwner) = await StartAsync();
        using var __ = other;
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)]);

        Assert.DoesNotContain(seeded.ConversationId, (await otherOwner.GetFromJsonAsync<InboxResponse>("/api/inbox"))!.Items.Select(i => i.Id));
        using var nobody = await f.Tenant.AddMemberAsync("inboxnobody");
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.ClientOf(nobody).GetAsync("/api/inbox")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.GetAsync($"/api/conversations/{seeded.ConversationId}/version")).StatusCode);
    }

    [Fact]
    public async Task Inbox_ASecondPollWithItsETag_Is304_UntilSomethingChanges()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)]);

        var first = await owner.GetAsync("/api/inbox");
        var etag = first.Headers.ETag!;

        using var again = new HttpRequestMessage(HttpMethod.Get, "/api/inbox");
        again.Headers.IfNoneMatch.Add(etag);
        var second = await owner.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, seeded.ConversationId, "Hola")).StatusCode);
        using var third = new HttpRequestMessage(HttpMethod.Get, "/api/inbox");
        third.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.OK, (await owner.SendAsync(third)).StatusCode);
    }

    [Fact]
    public async Task Inbox_Since_ReturnsOnlyWhatChanged_IncludingAConversationThatClosed()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        var quiet = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)]);
        var replied = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-4), [In(0)]);
        var closing = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-3), [In(0)]);
        var cursor = (await owner.GetFromJsonAsync<InboxResponse>("/api/inbox"))!.Cursor.AddSeconds(InboxServiceOverlapSeconds);

        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, replied.ConversationId, "Hola")).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == closing.ConversationId);
            conversation.Close(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        var delta = await owner.GetFromJsonAsync<InboxResponse>($"/api/inbox?since={Uri.EscapeDataString(cursor.ToString("O"))}");

        Assert.Equal([closing.ConversationId, replied.ConversationId], delta!.Items.Select(i => i.Id));
        Assert.Equal("Closed", delta.Items[0].Status);
        Assert.DoesNotContain(quiet.ConversationId, delta.Items.Select(i => i.Id));
        Assert.Null(delta.Items.FirstOrDefault(i => i.Id == replied.ConversationId && i.AwaitingReply));
    }

    private const int InboxServiceOverlapSeconds = 10;

    [Fact]
    public async Task Inbox_CostsTheSameNumberOfQueries_ForTwoOrTwelveConversations()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        for (var i = 0; i < 2; i++)
        {
            await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-30 + i), [In(0)]);
        }

        await owner.GetAsync("/api/inbox"); // warm-up: the first call compiles the queries
        var before = _factory.DbCommands.Count;
        await owner.GetAsync("/api/inbox");
        var withTwo = _factory.DbCommands.Count - before;

        for (var i = 0; i < 10; i++)
        {
            await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-20 + i), [In(0), Out(5)]);
        }

        before = _factory.DbCommands.Count;
        var response = await owner.GetFromJsonAsync<InboxResponse>("/api/inbox");
        var withTwelve = _factory.DbCommands.Count - before;

        Assert.Equal(12, response!.Items.Count);
        Assert.Equal(withTwo, withTwelve);
    }

    [Fact]
    public async Task ConversationVersion_ChangesWithANewMessage_AndSupports304()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)]);
        var url = $"/api/conversations/{seeded.ConversationId}/version";

        var first = await owner.GetAsync(url);
        var version = await first.Content.ReadFromJsonAsync<ConversationVersionResponse>();
        using var again = new HttpRequestMessage(HttpMethod.Get, url);
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await owner.SendAsync(again)).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, seeded.ConversationId, "Hola")).StatusCode);
        var after = await owner.GetFromJsonAsync<ConversationVersionResponse>(url);
        Assert.Equal(version!.MessageCount + 1, after!.MessageCount);
        Assert.NotEqual(version.Version, after.Version);
    }

    // ---- Read receipts and typing ----------------------------------------------------------------------

    [Fact]
    public async Task ReadReceipts_AreOffByDefault_AndSendNothing()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)]);

        Assert.False((await owner.GetFromJsonAsync<MessagingSettingsResponse>("/api/tenant/messaging-settings"))!.ReadReceiptsEnabled);
        var response = await (await owner.PostAsync($"/api/conversations/{seeded.ConversationId}/read", null)).Content.ReadFromJsonAsync<PresenceResponse>();

        Assert.Equal(PresenceOutcome.Disabled, response!.Outcome);
        Assert.Empty(_host.Sender.ReceiptsOf(f.Channel.Id));
    }

    [Fact]
    public async Task ReadReceipts_On_MarkTheLastCustomerMessageOnce_AndTypingSendsTheIndicator()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/tenant/messaging-settings", new { readReceiptsEnabled = true })).StatusCode);
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0), In(30, "¿Sigue disponible?")]);
        var lastInbound = await LastInboundIdAsync(seeded.ConversationId);

        var first = await (await owner.PostAsync($"/api/conversations/{seeded.ConversationId}/read", null)).Content.ReadFromJsonAsync<PresenceResponse>();
        var second = await (await owner.PostAsync($"/api/conversations/{seeded.ConversationId}/read", null)).Content.ReadFromJsonAsync<PresenceResponse>();
        var typing = await (await owner.PostAsync($"/api/conversations/{seeded.ConversationId}/typing", null)).Content.ReadFromJsonAsync<PresenceResponse>();
        var typingAgain = await (await owner.PostAsync($"/api/conversations/{seeded.ConversationId}/typing", null)).Content.ReadFromJsonAsync<PresenceResponse>();

        Assert.Equal([PresenceOutcome.Sent, PresenceOutcome.NotNeeded, PresenceOutcome.Sent, PresenceOutcome.Throttled],
            new[] { first!.Outcome, second!.Outcome, typing!.Outcome, typingAgain!.Outcome });
        var receipts = _host.Sender.ReceiptsOf(f.Channel.Id);
        Assert.Equal([(lastInbound, false), (lastInbound, true)], receipts.Select(r => (r.ProviderMessageId, r.Typing)));
    }

    [Fact]
    public async Task ReadReceipts_ProviderRefusal_IsAnOutcome_NotAnError()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        await owner.PutAsJsonAsync("/api/tenant/messaging-settings", new { readReceiptsEnabled = true });
        _host.Sender.AnswerReceipts(f.Channel.Id, WhatsAppSendStatus.Rejected);
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)]);

        var response = await owner.PostAsync($"/api/conversations/{seeded.ConversationId}/read", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PresenceOutcome.Failed, (await response.Content.ReadFromJsonAsync<PresenceResponse>())!.Outcome);
    }

    [Fact]
    public async Task ReadReceiptsSetting_NeedsOrganizationManage_AndReachesTheProfile()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        using var member = await f.Tenant.AddMemberAsync("rrmember", Permission.ConversationsView, Permission.ConversationsManage);

        Assert.Equal(HttpStatusCode.Forbidden, (await _host.ClientOf(member).PutAsJsonAsync("/api/tenant/messaging-settings", new { readReceiptsEnabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/tenant/messaging-settings", new { })).StatusCode);
        await owner.PutAsJsonAsync("/api/tenant/messaging-settings", new { readReceiptsEnabled = true });

        var profile = await owner.GetFromJsonAsync<Application.DTOs.Users.UserProfileResponse>("/api/users/me");
        Assert.True(profile!.TenantReadReceipts);
    }

    private async Task<string> LastInboundIdAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Direction == MessageDirection.Inbound)
            .OrderByDescending(m => m.SentAt).Select(m => m.ProviderMessageId).FirstAsync();
    }

    // ---- Polling load and delta semantics (review fixes 3 to 5) ----------------------------------------------

    [Fact]
    public async Task Inbox_AStillCurrentVersion_Is304_WithoutReadingTheList()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)]);
        var first = await owner.GetAsync("/api/inbox");

        var recorder = _inboxHost.Recorder;
        using var client = _inboxHost.ClientWith(f.Tenant.Owner.Token);
        recorder.Clear();
        using var again = new HttpRequestMessage(HttpMethod.Get, "/api/inbox");
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var response = await client.SendAsync(again);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.DoesNotContain(recorder.Texts, t => t.Contains("\"Contacts\"", StringComparison.Ordinal)); // the rows were never built
        Assert.Contains(recorder.Texts, t => t.Contains("max(\"UpdatedAt\")", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inbox_Since_FlagsAConversationReassignedOutOfTheFilter()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        var mine = await CreateAdvisorAsync(owner, f.Branch.Id, userId: f.Tenant.Owner.UserId);
        var other = await CreateAdvisorAsync(owner, f.Branch.Id);
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)], advisorId: mine.Id);
        var cursor = (await owner.GetFromJsonAsync<InboxResponse>("/api/inbox?mine=true"))!.Cursor;
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/conversations/{seeded.ConversationId}/advisor", new { advisorId = other.Id })).StatusCode);
        var delta = await owner.GetFromJsonAsync<InboxResponse>($"/api/inbox?mine=true&since={Uri.EscapeDataString(cursor.ToString("O"))}");

        var item = Assert.Single(delta!.Items, i => i.Id == seeded.ConversationId);
        Assert.False(item.InFilter);
        Assert.False(delta.FullReloadRequired);
    }

    [Fact]
    public async Task Inbox_ANewConversation_HasItsChangeTimeSet_SoTheDeltaFindsIt()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        var cursor = (await owner.GetFromJsonAsync<InboxResponse>("/api/inbox"))!.Cursor;
        await ClearQueueAsync(_factory.Services);

        await DeliverAsync(_client, f, Dialog360Samples.InboundText($"wamid.NEW-{Guid.NewGuid():N}", timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5, waId: UniqueWaId()));
        await DrainAsync(RunnerOf(_factory.Services));

        var delta = await owner.GetFromJsonAsync<InboxResponse>($"/api/inbox?since={Uri.EscapeDataString(cursor.ToString("O"))}");
        Assert.Single(delta!.Items);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Conversations.IgnoreQueryFilters().AnyAsync(c => c.TenantId == f.TenantId && c.UpdatedAt == null));
    }

    [Fact]
    public async Task Polling_IsRateLimitedPerUser()
    {
        var f = await StartChannelAsync(_factory, _client);
        using var _ = f;
        var limited = MessagingHostOf(_factory, "poll-limit", new() { ["Inbox:PollRateLimitPerMinute"] = "3", ["Messaging:PresenceRateLimitPerMinute"] = "2" });
        using var client = limited.ClientOf(f.Tenant.Owner);
        var seeded = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)]);

        var inbox = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            inbox.Add((await client.GetAsync("/api/inbox")).StatusCode);
        }

        var typing = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            typing.Add((await client.PostAsync($"/api/conversations/{seeded.ConversationId}/typing", null)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], inbox);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], typing);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/conversations/{seeded.ConversationId}/version")).StatusCode); // its own budget
    }

    [Fact]
    public async Task InboxQueries_UseTheirIndexes_NoSortOfAllOpenConversations()
    {
        var (f, owner) = await StartAsync();
        using var _ = f;
        await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)]);
        var cursor = (await owner.GetFromJsonAsync<InboxResponse>("/api/inbox"))!.Cursor;
        var recorder = _inboxHost.Recorder;
        using var client = _inboxHost.ClientWith(f.Tenant.Owner.Token);

        recorder.Clear();
        await client.GetAsync("/api/inbox");
        await client.GetAsync($"/api/inbox?since={Uri.EscapeDataString(cursor.ToString("O"))}");

        var plans = await recorder.ExplainAsync(_factory.Services, t => t.Contains("\"Conversations\"", StringComparison.Ordinal) && t.Contains("LIMIT", StringComparison.Ordinal));
        var all = string.Join("\n---\n", plans);
        Assert.True(plans.Count >= 4, all); // the change token, awaiting, the rest, the delta
        Assert.Contains("IX_Conversations_Inbox_Waiting", all);
        Assert.Contains("IX_Conversations_Inbox_Changes", all);
        Assert.DoesNotContain("Seq Scan on \"Conversations\"", all);

        // At most an incremental sort of the rows an index already returns in order (bounded by the LIMIT), never a sort of every open row.
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"(?<!Incremental )Sort  \(cost"), all);
    }

    private InboxHost _inboxHost => InboxHost.Of(_factory);
}
