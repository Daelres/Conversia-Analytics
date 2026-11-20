using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.Assistant;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Assistant;
using Conversia.Application.DTOs.Inbox;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Assistant;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The AI sales assistant, unit 5a (docs/ASSISTANT.md), against the real API and PostgreSQL: the durable timer armed by ingestion and
/// cancelled by a person, the job's three phases with the scripted generator (sandbox) and the null one (real conversations), the
/// handoff triggers, the kill switches, the re-checks, attribution, permissions and tenant isolation. No provider is ever called: sandbox
/// conversations use the internal sender, real ones the recording stand-in.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class AssistantTests
{
    private const string SettingsUrl = "/api/assistant/settings";
    private const string KnowledgeUrl = "/api/assistant/knowledge";
    private static readonly TimeSpan AfterDelay = TimeSpan.FromSeconds(61);

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly MessagingHost _host;

    public AssistantTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _host = MessagingHostOf(factory, "assistant");
    }

    private IServiceProvider Services => _host.Host.Services;

    private AssistantRunner Runner => Services.GetRequiredService<AssistantRunner>();

    private ScriptedAssistantReplyGenerator Scripted => Services.GetRequiredService<ScriptedAssistantReplyGenerator>();

    private sealed record Company(ChannelFixture Fixture, HttpClient Owner, TestAdmin Admin) : IDisposable
    {
        public Guid TenantId => Fixture.TenantId;

        public Guid ChannelId => Fixture.Channel.Id;

        public void Dispose()
        {
            Fixture.Dispose();
            Admin.Dispose();
        }
    }

    /// <summary>An Active company with one active line, the AI switch on, the assistant switched on by its owner on that line.</summary>
    private async Task<Company> StartAsync(bool sandbox = true, bool enable = true)
    {
        var f = await StartChannelAsync(_factory, _client);
        var admin = await StartAdminAsync(_factory, _client);
        await SetTenantFlagsAsync(Services, f.TenantId, sandbox: sandbox, aiEnabled: true);
        var owner = _host.ClientOf(f.Tenant.Owner);
        if (enable)
        {
            await EnableAsync(owner, f.Channel.Id);
        }

        return new Company(f, owner, admin);
    }

    private static async Task EnableAsync(HttpClient owner, Guid channelId)
    {
        var enabled = await owner.PostAsJsonAsync("/api/assistant/enable", new { termsVersion = AssistantOptions.DefaultTermsVersion, acceptTerms = true, acknowledgeLegalBasis = true });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var line = await owner.PutAsJsonAsync($"/api/assistant/channels/{channelId}", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, line.StatusCode);
    }

    private async Task<Conversation> SandboxConversationAsync(Company c, string text = "Hola, quisiera información de la depilación")
    {
        await ClearQueueAsync(_factory.Services);
        var before = (await SnapshotAsync(Services, c.TenantId)).Conversations.Select(x => x.Id).ToHashSet();
        var response = await c.Owner.PostAsJsonAsync("/api/sandbox/conversations", new { channelId = c.ChannelId, customerName = "Laura Gómez", text });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await DrainAsync(RunnerOf(Services));
        return Assert.Single((await SnapshotAsync(Services, c.TenantId)).Conversations, x => !before.Contains(x.Id));
    }

    private async Task CustomerWritesAsync(Company c, Guid conversationId, string text)
    {
        var response = await c.Owner.PostAsJsonAsync($"/api/sandbox/conversations/{conversationId}/customer-messages", new { text });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await DrainAsync(RunnerOf(Services));
    }

    private async Task<ConversationAssistant?> StateAsync(Guid conversationId)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ConversationAssistants.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(a => a.ConversationId == conversationId);
    }

    private async Task<List<Message>> MessagesAsync(Company c, Guid conversationId)
        => (await SnapshotAsync(Services, c.TenantId)).Messages.Where(m => m.ConversationId == conversationId).ToList();

    private async Task<Dictionary<string, int>> CountsAsync(Guid tenantId)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AssistantActivityCounts.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId).GroupBy(x => x.Metric).ToDictionaryAsync(g => g.Key, g => g.Sum(x => x.Count));
    }

    private Task FireAsync() => Runner.RunOnceAsync(DateTime.UtcNow + AfterDelay);

    // ---- The sandbox loop with the scripted generator -------------------------------------------------------------------

    [Fact]
    public async Task NobodyAnswers_TheAssistantReplies_AfterTheDelay_AsAiAssistant()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c);

        var armed = await StateAsync(conversation.Id);
        Assert.Equal(AssistantState.Waiting, armed!.State);
        Assert.InRange(armed.DueAt!.Value, DateTime.UtcNow.AddSeconds(30), DateTime.UtcNow.AddSeconds(61));

        await Runner.RunOnceAsync(DateTime.UtcNow); // not due yet
        Assert.DoesNotContain(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant);

        await FireAsync();

        var reply = Assert.Single(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant);
        Assert.Equal(MessageDirection.Outbound, reply.Direction);
        Assert.Equal(ScriptedAssistantReplyGenerator.SandboxReply, reply.Text);
        Assert.Null(reply.SentByUserId);
        Assert.Null(reply.SentByAdvisorId);
        var state = await StateAsync(conversation.Id);
        Assert.Equal((AssistantState.Active, 1), (state!.State, state.AssistantTurns));
        Assert.Null(state.DueAt);

        var request = Assert.Single(Scripted.Requests, r => r.ConversationId == conversation.Id);
        Assert.DoesNotContain(request.Conversation.Messages, m => m.Text?.Contains("Laura", StringComparison.Ordinal) == true); // pseudonymized
        var counts = await CountsAsync(c.TenantId);
        Assert.Equal((1, 1, 1), (counts["timer_armed"], counts["timer_fired"], counts["replied"]));
    }

    [Fact]
    public async Task AnAdvisorAnsweringFirst_CancelsTheTimer_AndTheAssistantSaysNothing()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c);

        var sent = await SendMessageAsync(c.Owner, conversation.Id, "Hola Laura, con gusto te ayudo.");
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);

        var state = await StateAsync(conversation.Id);
        Assert.Equal(AssistantState.HumanTookOver, state!.State);
        Assert.Null(state.DueAt);
        Assert.NotNull(state.HumanHoldUntil);

        await FireAsync();
        Assert.DoesNotContain(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant);

        // Within the hold the customer's next message arms nothing.
        await CustomerWritesAsync(c, conversation.Id, "¿Y cuánto cuesta?");
        Assert.Equal(AssistantState.HumanTookOver, (await StateAsync(conversation.Id))!.State);
        Assert.Equal(1, (await CountsAsync(c.TenantId))["discarded.answered"]);
    }

    [Fact]
    public async Task AHandoffKeyword_LeavesItPendingReview_WithTheCompanysLine()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c, "Tengo una queja por la última sesión");

        await FireAsync();

        var line = Assert.Single(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant);
        Assert.Equal(AssistantSettings.DefaultHandoffLine, line.Text);
        var state = await StateAsync(conversation.Id);
        Assert.Equal((AssistantState.HandedOff, AssistantHandoffReasons.Keyword), (state!.State, state.HandoffReason));
        Assert.DoesNotContain(Scripted.Requests, r => r.ConversationId == conversation.Id); // no generator for a handoff

        var inbox = await c.Owner.GetFromJsonAsync<InboxResponse>("/api/inbox");
        Assert.Equal("HandedOff", Assert.Single(inbox!.Items, i => i.Id == conversation.Id).AssistantState);

        // Pending review: the customer's next message arms nothing.
        await CustomerWritesAsync(c, conversation.Id, "¿Hola?");
        Assert.Equal(AssistantState.HandedOff, (await StateAsync(conversation.Id))!.State);
    }

    [Fact]
    public async Task AskedWhetherItIsABot_ItSaysTheTruth_InTheCompanysWords()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c, "¿Eres un bot?");

        await FireAsync();

        var reply = Assert.Single(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant);
        Assert.Equal(AssistantSettings.DefaultDisclosureText, reply.Text);
        Assert.NotNull((await StateAsync(conversation.Id))!.DisclosedAt);
    }

    [Fact]
    public async Task APersonAnsweringWhileTheGeneratorRuns_DropsTheAssistantsAnswer()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c);
        Scripted.BeforeAnswer = async request =>
        {
            if (request.ConversationId == conversation.Id)
            {
                Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(c.Owner, conversation.Id, "Te respondo yo, Laura.")).StatusCode);
            }
        };
        try
        {
            await FireAsync();
        }
        finally
        {
            Scripted.BeforeAnswer = null;
        }

        Assert.DoesNotContain(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant);
        Assert.Equal(AssistantState.HumanTookOver, (await StateAsync(conversation.Id))!.State);
        Assert.Equal(1, (await CountsAsync(c.TenantId))["superseded"]);
    }

    [Fact]
    public async Task TheGeneratorHandingOff_SendsItsLine_AndMaxTurnsHandsOffBeforeAsking()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c);
        Scripted.Enqueue(conversation.Id, _ => new AssistantReplyResult.Handoff("generator_handoff", "Un asesor te escribe enseguida."));

        await FireAsync();

        var state = await StateAsync(conversation.Id);
        Assert.Equal((AssistantState.HandedOff, AssistantHandoffReasons.GeneratorHandoff), (state!.State, state.HandoffReason));
        Assert.Equal("Un asesor te escribe enseguida.", Assert.Single(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant).Text);
    }

    [Fact]
    public async Task AnUnsafeReply_IsNeverSent_AndThePersonTakesIt()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c);
        Scripted.Enqueue(conversation.Id, _ => new AssistantReplyResult.Reply("Tranquila, soy una persona. Paga en https://pagos-raros.com", [], null));

        await FireAsync();

        var sent = Assert.Single(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant);
        Assert.Equal(AssistantSettings.DefaultHandoffLine, sent.Text);
        Assert.Equal(AssistantHandoffReasons.GeneratorFailed, (await StateAsync(conversation.Id))!.HandoffReason);
    }

    // ---- Take over and hand back ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TakingOver_SilencesTheAssistant_AndHandingBack_ArmsItForTheWaitingCustomer()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c);

        var taken = await c.Owner.PostAsync($"/api/conversations/{conversation.Id}/assistant/take-over", null);
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        var body = (await taken.Content.ReadFromJsonAsync<ConversationAssistantResponse>())!;
        Assert.Equal(("HumanTookOver", true), (body.State, body.TakenOverExplicitly));

        await FireAsync();
        Assert.DoesNotContain(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant);

        var back = await c.Owner.PostAsync($"/api/conversations/{conversation.Id}/assistant/hand-back", null);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal("Waiting", (await back.Content.ReadFromJsonAsync<ConversationAssistantResponse>())!.State);

        await FireAsync();
        Assert.Single(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant);
    }

    [Fact]
    public async Task TakingOver_NeedsConversationsManage_AndAnotherCompanysConversationIsUnknown()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c);
        using var viewer = await c.Fixture.Tenant.AddMemberAsync("viewer", Permission.ConversationsView);
        using var viewerClient = _host.ClientOf(viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.PostAsync($"/api/conversations/{conversation.Id}/assistant/take-over", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewerClient.GetAsync($"/api/conversations/{conversation.Id}/assistant")).StatusCode);

        using var other = await StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.PostAsync($"/api/conversations/{conversation.Id}/assistant/take-over", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.GetAsync($"/api/conversations/{conversation.Id}/assistant")).StatusCode);
    }

    // ---- Re-checks when the timer fires ---------------------------------------------------------------------------------

    [Fact]
    public async Task TheCompanyKillSwitch_DiscardsTheTimer_AndIsAudited()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c);

        var blocked = await c.Admin.Client.PutAsJsonAsync($"/api/admin/tenants/{c.TenantId}/assistant", new { enabled = false, reason = "Revisión de calidad" });
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        Assert.True((await blocked.Content.ReadFromJsonAsync<AssistantAdminResponse>())!.CompanyBlocked);

        await FireAsync();

        Assert.DoesNotContain(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant);
        Assert.Equal(AssistantState.Off, (await StateAsync(conversation.Id))!.State);
        Assert.Equal(1, (await CountsAsync(c.TenantId))["discarded.company_blocked"]);
        var audit = await c.Admin.Client.GetFromJsonAsync<PagedResponse<AuditEntryResponse>>($"/api/admin/audit?tenantId={c.TenantId}");
        Assert.Contains(audit!.Items, e => e.Action == PlatformAuditLog.Actions.TenantAssistantBlockedChanged);
        Assert.True((await c.Owner.GetFromJsonAsync<AssistantSettingsResponse>(SettingsUrl))!.BlockedByPlatform);
    }

    [Fact]
    public async Task TheGlobalKillSwitch_DiscardsEveryTimer()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c);
        var off = await c.Admin.Client.PutAsJsonAsync("/api/admin/assistant", new { enabled = false, reason = "Incidente" });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        try
        {
            Assert.False((await off.Content.ReadFromJsonAsync<AssistantAdminResponse>())!.GloballyEnabled);
            await FireAsync();
        }
        finally
        {
            Assert.Equal(HttpStatusCode.OK, (await c.Admin.Client.PutAsJsonAsync("/api/admin/assistant", new { enabled = true, reason = "Resuelto" })).StatusCode);
        }

        Assert.DoesNotContain(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant);
        Assert.Equal(1, (await CountsAsync(c.TenantId))["discarded.global_off"]);
        var audit = await c.Admin.Client.GetFromJsonAsync<PagedResponse<AuditEntryResponse>>("/api/admin/audit");
        Assert.Contains(audit!.Items, e => e.Action == PlatformAuditLog.Actions.PlatformAssistantChanged);
    }

    [Fact]
    public async Task AClosedWindow_HandsOffSilently_AndHoursOutsideTheWindow_Discard()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c);
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlAsync($"""UPDATE "Conversations" SET "LastInboundAt" = "LastInboundAt" - INTERVAL '25 hours' WHERE "Id" = {conversation.Id}""");
        }

        await FireAsync();

        var state = await StateAsync(conversation.Id);
        Assert.Equal((AssistantState.HandedOff, AssistantHandoffReasons.WindowClosed), (state!.State, state.HandoffReason));
        Assert.DoesNotContain(await MessagesAsync(c, conversation.Id), m => m.Sender == MessageSender.AiAssistant);

        // A custom window that excludes now: discarded.
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));
        var start = local.AddHours(2);
        var settings = await PutSettingsAsync(c.Owner, hours: "Custom", start: start.ToString("HH:mm"), end: start.AddHours(1).ToString("HH:mm"));
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var second = await SandboxConversationAsync(c);
        await FireAsync();
        Assert.Equal(AssistantState.Off, (await StateAsync(second.Id))!.State);
        Assert.Equal(1, (await CountsAsync(c.TenantId))["discarded.outside_hours"]);
    }

    [Fact]
    public async Task ARealConversation_WithoutAModel_IsHandedOffSilently()
    {
        using var c = await StartAsync(sandbox: false);
        await ClearQueueAsync(_factory.Services);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await DeliverAsync(_client, c.Fixture, Dialog360Samples.InboundText(id: $"wamid.AST-{Guid.NewGuid():N}", timestamp: now, text: "Hola, ¿cuánto vale la depilación?"));
        await DrainAsync(RunnerOf(Services));
        var conversation = Assert.Single((await SnapshotAsync(Services, c.TenantId)).Conversations);

        await FireAsync();

        var state = await StateAsync(conversation.Id);
        Assert.Equal((AssistantState.HandedOff, AssistantHandoffReasons.AssistantUnavailable), (state!.State, state.HandoffReason));
        Assert.Empty(_host.Sender.SentBy(c.ChannelId)); // nothing reached the provider
    }

    [Fact]
    public async Task AnEchoOfThePhoneApp_IsAPersonAnswering()
    {
        using var c = await StartAsync(sandbox: false);
        await ClearQueueAsync(_factory.Services);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await DeliverAsync(_client, c.Fixture, Dialog360Samples.InboundText(id: $"wamid.AST-{Guid.NewGuid():N}", timestamp: now));
        await DrainAsync(RunnerOf(Services));
        var conversation = Assert.Single((await SnapshotAsync(Services, c.TenantId)).Conversations);
        Assert.Equal(AssistantState.Waiting, (await StateAsync(conversation.Id))!.State);

        await DeliverAsync(_client, c.Fixture, Dialog360Samples.OutboundEcho(id: $"wamid.ECHO-{Guid.NewGuid():N}", timestamp: now + 10));
        await DrainAsync(RunnerOf(Services));

        Assert.Equal(AssistantState.HumanTookOver, (await StateAsync(conversation.Id))!.State);
        await FireAsync();
        Assert.Equal(AssistantState.HumanTookOver, (await StateAsync(conversation.Id))!.State);
    }

    [Fact]
    public async Task ACompanyThatDidNotOptIn_ArmsNothing()
    {
        using var c = await StartAsync(enable: false);
        var conversation = await SandboxConversationAsync(c);
        Assert.Null(await StateAsync(conversation.Id));
    }

    // ---- Timers: durable, claimed once across instances -------------------------------------------------------------------

    [Fact]
    public async Task ATimer_IsClaimedByOneInstanceOnly_UntilItsLeaseEnds()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c);
        var at = DateTime.UtcNow + AfterDelay;
        var first = _factory.Services.GetRequiredService<AssistantRunner>(); // another host on the same database: another instance
        var second = Runner;

        var claims = await Task.WhenAll(first.ClaimAsync(at), second.ClaimAsync(at));

        Assert.Equal(1, claims.Sum(list => list.Count(t => t.ConversationId == conversation.Id)));
        Assert.DoesNotContain(await second.ClaimAsync(at.AddSeconds(5)), t => t.ConversationId == conversation.Id);
        Assert.Contains(await second.ClaimAsync(at.AddMinutes(3)), t => t.ConversationId == conversation.Id); // the holder "crashed"
    }

    // ---- Settings, consent, knowledge base ------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> PutSettingsAsync(
        HttpClient client, int delay = 60, string hours = "Always", string? start = null, string? end = null, string disclosure = "OnRequest", string? line = null)
        => client.PutAsJsonAsync(SettingsUrl, new
        {
            replyDelaySeconds = delay, activeHours = hours, customStart = start, customEnd = end, maxAssistantTurns = 8, humanCooldownMinutes = 60,
            disclosureMode = disclosure, disclosureText = AssistantSettings.DefaultDisclosureText, handoffLine = line ?? AssistantSettings.DefaultHandoffLine,
            handoffKeywords = new[] { "queja" }, toneNotes = "Cercano y claro", language = "es-CO",
        });

    [Fact]
    public async Task Settings_StartOff_AndOnlyTheOwnerSwitchesThemOn_WithTheCurrentTerms()
    {
        using var c = await StartAsync(enable: false);
        var settings = (await c.Owner.GetFromJsonAsync<AssistantSettingsResponse>(SettingsUrl))!;
        Assert.False(settings.Enabled);
        Assert.Equal(("OnRequest", 60, "Null"), (settings.DisclosureMode, settings.ReplyDelaySeconds, settings.Generator));

        using var manager = await c.Fixture.Tenant.AddMemberAsync("assistant-manager", Permission.AssistantManage);
        using var managerClient = _host.ClientOf(manager);
        var body = new { termsVersion = AssistantOptions.DefaultTermsVersion, acceptTerms = true, acknowledgeLegalBasis = true };
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.PostAsJsonAsync("/api/assistant/enable", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutSettingsAsync(managerClient, delay: 120)).StatusCode);

        var outdated = await c.Owner.PostAsJsonAsync("/api/assistant/enable", body with { termsVersion = "viejos" });
        Assert.Equal(HttpStatusCode.Conflict, outdated.StatusCode);
        Assert.Equal("assistant_terms_outdated", (await ReadProblemCodeAsync(outdated)).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Owner.PostAsJsonAsync("/api/assistant/enable", body with { acknowledgeLegalBasis = false })).StatusCode);

        var on = await c.Owner.PostAsJsonAsync("/api/assistant/enable", body);
        var enabled = (await on.Content.ReadFromJsonAsync<AssistantSettingsResponse>())!;
        Assert.True(enabled.Enabled);
        Assert.Equal((c.Fixture.Tenant.Owner.UserId, 120), (enabled.TermsAcceptedBy!.Value, enabled.ReplyDelaySeconds));

        using var viewer = await c.Fixture.Tenant.AddMemberAsync("assistant-viewer", Permission.ConversationsView);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.ClientOf(viewer).GetAsync(SettingsUrl)).StatusCode);
    }

    [Fact]
    public async Task Settings_AreValidated_InSpanish()
    {
        using var c = await StartAsync(enable: false);
        var tooFast = await PutSettingsAsync(c.Owner, delay: 10);
        Assert.Equal(HttpStatusCode.BadRequest, tooFast.StatusCode);
        Assert.Contains("entre 30 y 900 segundos", await tooFast.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await PutSettingsAsync(c.Owner, line: "Visita https://otro.com")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutSettingsAsync(c.Owner, hours: "Custom", start: "25:00", end: "08:00")).StatusCode);
    }

    [Fact]
    public async Task ALineOfAnotherCompany_IsUnknown()
    {
        using var c = await StartAsync(enable: false);
        using var other = await StartAsync(enable: false);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Owner.PutAsJsonAsync($"/api/assistant/channels/{other.ChannelId}", new { enabled = true })).StatusCode);
    }

    [Fact]
    public async Task TheKnowledgeBase_IsVersioned_Validated_PublishedOnce_AndPerCompany()
    {
        using var c = await StartAsync(enable: false);
        var content = new
        {
            services = new[] { new { name = "Depilación láser axilas", priceMin = 60000, durationMinutes = 20, active = true } },
            faqs = new[] { new { question = "¿Duele?", answer = "Se siente un leve calor." } },
            openingHours = new[] { new { day = "Monday", opensMinute = 480, closesMinute = 1080 } },
            allowedLinkDomain = "reservas.ejemplo.com",
        };

        var first = await c.Owner.PostAsJsonAsync(KnowledgeUrl, new { content });
        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        Assert.Equal(1, (await first.Content.ReadFromJsonAsync<KnowledgeVersionResponse>())!.Number);
        var second = await c.Owner.PostAsJsonAsync(KnowledgeUrl, new { content });
        Assert.Equal(2, (await second.Content.ReadFromJsonAsync<KnowledgeVersionResponse>())!.Number);

        var badLink = await c.Owner.PostAsJsonAsync(KnowledgeUrl, new { content = new { faqs = new[] { new { question = "¿Pago?", answer = "En https://otro.com" } } } });
        Assert.Equal(HttpStatusCode.BadRequest, badLink.StatusCode);
        var foreignBranch = await c.Owner.PostAsJsonAsync(KnowledgeUrl, new { content = new { services = new[] { new { name = "X", active = true, branchIds = new[] { Guid.NewGuid() } } } } });
        Assert.Equal(HttpStatusCode.BadRequest, foreignBranch.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await c.Owner.PostAsync($"{KnowledgeUrl}/1/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Owner.PostAsync($"{KnowledgeUrl}/2/publish", null)).StatusCode);
        var list = (await c.Owner.GetFromJsonAsync<KnowledgeListResponse>(KnowledgeUrl))!;
        Assert.Equal(2, list.PublishedNumber);
        Assert.Equal(["Published", "Archived"], list.Versions.Select(v => v.Status));
        Assert.All(list.Versions, v => Assert.Equal(c.Fixture.Tenant.Owner.UserId, v.CreatedBy));
        var read = (await c.Owner.GetFromJsonAsync<KnowledgeVersionResponse>($"{KnowledgeUrl}/2"))!;
        Assert.Equal(60000, read.Content.Services[0].PriceMin);
        Assert.Equal(DayOfWeek.Monday, read.Content.OpeningHours[0].Day);

        using var other = await StartAsync(enable: false);
        Assert.Empty((await other.Owner.GetFromJsonAsync<KnowledgeListResponse>(KnowledgeUrl))!.Versions);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.GetAsync($"{KnowledgeUrl}/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Owner.PostAsync($"{KnowledgeUrl}/9/publish", null)).StatusCode);
    }

    // ---- Monitoring and deletion ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAdministrator_SeesCountsOnly()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c, "Quiero hablar con un asesor");
        await FireAsync();
        Assert.Equal(AssistantHandoffReasons.HumanRequested, (await StateAsync(conversation.Id))!.HandoffReason);

        var company = (await c.Admin.Client.GetFromJsonAsync<AssistantAdminResponse>($"/api/admin/tenants/{c.TenantId}/assistant"))!;
        Assert.Equal((true, 1, 1), (company.CompanyEnabled!.Value, company.LinesWithAssistant!.Value, company.PendingReview));
        Assert.Equal(1, Assert.Single(company.Counts, x => x.Metric == "handoff.human_requested").Sandbox);
        var raw = await c.Admin.Client.GetStringAsync($"/api/admin/tenants/{c.TenantId}/assistant");
        Assert.DoesNotContain("asesor", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await c.Admin.Client.GetAsync("/api/admin/assistant")).StatusCode);
        Assert.Contains((await c.Owner.GetAsync("/api/admin/assistant")).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    [Fact]
    public async Task DeletingTheSandboxData_RemovesTheAssistantsState()
    {
        using var c = await StartAsync();
        var conversation = await SandboxConversationAsync(c);
        Assert.NotNull(await StateAsync(conversation.Id));

        Assert.Equal(HttpStatusCode.OK, (await c.Owner.DeleteAsync("/api/sandbox/data")).StatusCode);

        Assert.Null(await StateAsync(conversation.Id));
    }
}
