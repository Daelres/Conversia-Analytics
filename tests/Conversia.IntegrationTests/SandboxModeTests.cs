using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Sandbox;
using Conversia.Application.DTOs.Tenants;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Classification;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Messaging;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The internal sandbox mode (docs/SANDBOX.md) against the real API and PostgreSQL: who switches it, the simulated customer through the
/// real ingestion pipeline, the internal sender that never reaches 360dialog, the wall between sandbox and real data, the metrics on
/// each side of that wall, and the clean-up. The host here keeps the REAL sender resolver and the REAL 360dialog adapter; only the
/// transport under the adapter is replaced by one that records, so "no request left" is asserted on the wire.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public partial class SandboxModeTests
{
    private const string SandboxModeOff = "sandbox_mode_off";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly SandboxHost _host;

    public SandboxModeTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _host = factory.GetOrAddHost("sandbox-mode", () => SandboxHost.Create(factory));
    }

    /// <summary>What would have gone to 360dialog: every request the adapter's HttpClient was asked to send.</summary>
    private sealed class ProviderWire : HttpMessageHandler
    {
        private readonly ConcurrentQueue<(Uri Uri, string? Key, string Body)> _requests = new();

        /// <summary>
        /// The requests made with one API key. Every test stores a key of its own, so what it counts is its own traffic whatever else
        /// uses this host (the classes of the collection run one after the other, but nothing here depends on that).
        /// </summary>
        public IReadOnlyList<(Uri Uri, string? Key, string Body)> WithKey(string key) => _requests.Where(r => r.Key == key).ToArray();

        public int CountContaining(string text) => _requests.Count(r => r.Body.Contains(text, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Headers.TryGetValues(Dialog360MessageSender.ApiKeyHeader, out var values) ? values.FirstOrDefault() : null;
            _requests.Enqueue((request.RequestUri!, key, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent($$"""{"messaging_product":"whatsapp","messages":[{"id":"wamid.WIRE-{{Guid.NewGuid():N}}"}]}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record SandboxHost(WebApplicationFactory<Program> Host, ProviderWire Wire)
    {
        public static SandboxHost Create(CustomWebApplicationFactory factory)
        {
            var wire = new ProviderWire();
            var host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting(EncryptionKeySetting, TestEncryptionKey);
                builder.ConfigureServices(services =>
                    services.AddHttpClient<Dialog360MessageSender>().ConfigurePrimaryHttpMessageHandler(() => wire));
            });
            return new SandboxHost(host, wire);
        }

        public HttpClient ClientOf(TestActor actor)
        {
            var client = Host.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
            return client;
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    private static string SandboxModeUrl(Guid tenantId) => $"{AdminTenantsUrl}/{tenantId}/sandbox-mode";

    private const string StartUrl = "/api/sandbox/conversations";

    private const string DataUrl = "/api/sandbox/data";

    private static string CustomerUrl(Guid conversationId) => $"/api/sandbox/conversations/{conversationId}/customer-messages";

    private static async Task SetSandboxModeAsync(TestAdmin admin, Guid tenantId, bool enabled, string reason = "Pruebas de integración")
    {
        var response = await admin.Client.PutAsJsonAsync(SandboxModeUrl(tenantId), new { enabled, reason });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<TestAdmin> AdminAsync() => StartAdminAsync(_factory, _client);

    /// <summary>A company with one active channel, in sandbox mode unless told otherwise, and its owner's client on the sandbox host.</summary>
    private async Task<(ChannelFixture Fixture, HttpClient Owner, TestAdmin Admin)> StartAsync(bool sandboxMode = true)
    {
        var f = await StartChannelAsync(_factory, _client);
        var admin = await AdminAsync();
        if (sandboxMode)
        {
            await SetSandboxModeAsync(admin, f.TenantId, true);
        }

        return (f, _host.ClientOf(f.Tenant.Owner), admin);
    }

    private static Task<HttpResponseMessage> StartConversationAsync(HttpClient client, Guid channelId, string? name = "Laura Gómez", string? text = "Hola, quisiera información")
        => client.PostAsJsonAsync(StartUrl, new { channelId, customerName = name, text });

    private static Task<HttpResponseMessage> CustomerWritesAsync(HttpClient client, Guid conversationId, string? text = "¿Y cuánto cuesta?")
        => client.PostAsJsonAsync(CustomerUrl(conversationId), new { text });

    /// <summary>Processes the queue the way the worker does: the same runner, called by hand like in every other ingestion test.</summary>
    private async Task IngestAsync() => await DrainAsync(RunnerOf(_host.Host.Services));

    /// <summary>Starts a simulated conversation and ingests it; returns the stored conversation.</summary>
    private async Task<Conversation> StartIngestedAsync(ChannelFixture f, HttpClient owner, string text = "Hola, quisiera información")
    {
        await ClearQueueAsync(_factory.Services);
        var before = (await SnapshotAsync(_factory.Services, f.TenantId)).Conversations.Select(c => c.Id).ToHashSet();
        Assert.Equal(HttpStatusCode.Accepted, (await StartConversationAsync(owner, f.Channel.Id, text: text)).StatusCode);
        await IngestAsync();
        return Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations, c => !before.Contains(c.Id));
    }

    /// <summary>A provider key no other test uses, in the shape the API accepts.</summary>
    private static string NewProviderKey() => $"d360-{Guid.NewGuid():N}";

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) => (await ReadProblemCodeAsync(response)).Code;

    // ---- Who switches the mode ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAdministrator_SwitchesTheMode_WithAReason_AndItIsAuditedAndVisibleEverywhere()
    {
        using var tenant = await StartTenantAsync(_factory, _client, "sandbox");
        using var admin = await AdminAsync();

        Assert.False((await GetAdminTenantAsync(admin, tenant.TenantId)).SandboxMode);
        Assert.False((await tenant.Owner.Client.GetFromJsonAsync<TenantInfoResponse>("/api/tenant"))!.SandboxMode);

        var response = await admin.Client.PutAsJsonAsync(SandboxModeUrl(tenant.TenantId), new { enabled = true, reason = "  Demo con el cliente piloto  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadBodyAsync<TenantSummaryResponse>(response)).SandboxMode);
        Assert.True((await GetAdminTenantAsync(admin, tenant.TenantId)).SandboxMode);
        Assert.True((await tenant.Owner.Client.GetFromJsonAsync<TenantInfoResponse>("/api/tenant"))!.SandboxMode);

        var audit = await admin.Client.GetFromJsonAsync<PagedResponse<AuditEntryResponse>>($"/api/admin/audit?tenantId={tenant.TenantId}");
        var entry = Assert.Single(audit!.Items);
        Assert.Equal("tenant.sandbox_mode_changed", entry.Action);
        Assert.Equal((admin.AdminId, tenant.TenantId), (entry.AdminId, entry.TenantId!.Value));
        Assert.False(entry.Details.GetProperty("from").GetBoolean());
        Assert.True(entry.Details.GetProperty("to").GetBoolean());
        Assert.Equal("Demo con el cliente piloto", entry.Details.GetProperty("reason").GetString());

        // Asking for what it already is writes nothing; switching it off is a second entry.
        await SetSandboxModeAsync(admin, tenant.TenantId, true, "otra vez");
        Assert.Equal(1, (await admin.Client.GetFromJsonAsync<PagedResponse<AuditEntryResponse>>($"/api/admin/audit?tenantId={tenant.TenantId}"))!.TotalCount);
        await SetSandboxModeAsync(admin, tenant.TenantId, false, "fin de la demo");
        Assert.Equal(2, (await admin.Client.GetFromJsonAsync<PagedResponse<AuditEntryResponse>>($"/api/admin/audit?tenantId={tenant.TenantId}"))!.TotalCount);
        Assert.False((await GetAdminTenantAsync(admin, tenant.TenantId)).SandboxMode);
    }

    [Fact]
    public async Task TheListing_SaysWhichCompaniesAreInSandboxMode()
    {
        using var tenant = await StartTenantAsync(_factory, _client, "sandbox");
        using var admin = await AdminAsync();
        await SetSandboxModeAsync(admin, tenant.TenantId, true);

        var name = (await GetAdminTenantAsync(admin, tenant.TenantId)).Name;
        var listed = await admin.Client.GetFromJsonAsync<PagedResponse<TenantSummaryResponse>>($"{AdminTenantsUrl}?search={Uri.EscapeDataString(name)}");

        Assert.True(Assert.Single(listed!.Items, t => t.Id == tenant.TenantId).SandboxMode);
    }

    [Fact]
    public async Task TheMode_NeedsAValueAndAReasonOfAtMost200Characters()
    {
        using var tenant = await StartTenantAsync(_factory, _client, "sandbox");
        using var admin = await AdminAsync();
        var url = SandboxModeUrl(tenant.TenantId);

        await AssertValidationErrorUnderAsync(await admin.Client.PutAsJsonAsync(url, new { enabled = true }), "reason");
        await AssertValidationErrorUnderAsync(await admin.Client.PutAsJsonAsync(url, new { enabled = true, reason = "   " }), "reason");
        await AssertValidationErrorUnderAsync(await admin.Client.PutAsJsonAsync(url, new { enabled = true, reason = new string('x', 201) }), "reason");
        await AssertValidationErrorUnderAsync(await admin.Client.PutAsJsonAsync(url, new { reason = "sin valor" }), "enabled");
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync(url, new { enabled = true, reason = new string('x', 200) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PutAsJsonAsync(SandboxModeUrl(Guid.NewGuid()), new { enabled = true, reason = "x" })).StatusCode);

        Assert.True((await GetAdminTenantAsync(admin, tenant.TenantId)).SandboxMode);
    }

    [Fact]
    public async Task ACompany_CannotSwitchItsOwnMode_ByAnyRoute()
    {
        using var tenant = await StartTenantAsync(_factory, _client, "sandbox");
        using var admin = await AdminAsync();
        var body = new { enabled = true, reason = "quiero probar", sandboxMode = true };

        // The administrator's route refuses a company's token (and no token).
        Assert.Contains(
            (await tenant.Owner.Client.PutAsJsonAsync(SandboxModeUrl(tenant.TenantId), body)).StatusCode,
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PutAsJsonAsync(SandboxModeUrl(tenant.TenantId), body)).StatusCode);

        // The company's own API has nothing that writes it.
        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Put, "/api/tenant"), (HttpMethod.Post, "/api/tenant"), (HttpMethod.Patch, "/api/tenant"),
                     (HttpMethod.Put, "/api/tenant/sandbox-mode"), (HttpMethod.Put, "/api/sandbox/mode"), (HttpMethod.Post, "/api/sandbox/mode"),
                     (HttpMethod.Put, "/api/sandbox"), (HttpMethod.Post, "/api/sandbox"),
                 })
        {
            using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
            Assert.Contains((await tenant.Owner.Client.SendAsync(request)).StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }

        Assert.False((await GetAdminTenantAsync(admin, tenant.TenantId)).SandboxMode);
        Assert.False((await tenant.Owner.Client.GetFromJsonAsync<TenantInfoResponse>("/api/tenant"))!.SandboxMode);
    }

    // ---- The simulated customer: refusals --------------------------------------------------------------------------------

    [Fact]
    public async Task OutsideSandboxMode_TheSimulatedCustomerDoesNotExist_409WithItsCode_AndNothingIsStored()
    {
        var (f, owner, admin) = await StartAsync(sandboxMode: false);
        using var _ = f;
        using var __ = admin;
        var real = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-1), [In(0)]);
        var sandbox = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-1), [In(0)], sandbox: true);

        var start = await StartConversationAsync(owner, f.Channel.Id);
        var onReal = await CustomerWritesAsync(owner, real.ConversationId);
        var onSandbox = await CustomerWritesAsync(owner, sandbox.ConversationId);

        Assert.All(new[] { start, onReal, onSandbox }, r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(SandboxModeOff, await CodeOfAsync(start));
        Assert.Equal(SandboxModeOff, await CodeOfAsync(onReal));
        Assert.Equal(SandboxModeOff, await CodeOfAsync(onSandbox));
        Assert.Empty((await SnapshotAsync(_factory.Services, f.TenantId)).Events);
    }

    [Fact]
    public async Task TheSimulatedCustomer_NeedsConversationsManage_AndTheCleanUpNeedsOrganizationManage()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        using var viewer = await f.Tenant.AddMemberAsync("viewer", Permission.ConversationsView, Permission.OrganizationView);
        using var manager = await f.Tenant.AddMemberAsync("manager", Permission.ConversationsManage);
        var conversation = await StartIngestedAsync(f, owner);

        Assert.Equal(HttpStatusCode.Unauthorized, (await StartConversationAsync(_client, f.Channel.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CustomerWritesAsync(_client, conversation.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.DeleteAsync(DataUrl)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await StartConversationAsync(viewer.Client, f.Channel.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CustomerWritesAsync(viewer.Client, conversation.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.DeleteAsync(DataUrl)).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await StartConversationAsync(manager.Client, f.Channel.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await CustomerWritesAsync(manager.Client, conversation.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Client.DeleteAsync(DataUrl)).StatusCode);
    }

    [Fact]
    public async Task TheSimulatedCustomer_ValidatesItsBody()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        var conversation = await StartIngestedAsync(f, owner);

        await AssertValidationErrorUnderAsync(await owner.PostAsJsonAsync(StartUrl, new { customerName = "Laura", text = "Hola" }), "channelId");
        await AssertValidationErrorUnderAsync(await StartConversationAsync(owner, f.Channel.Id, name: " "), "customerName");
        await AssertValidationErrorUnderAsync(await StartConversationAsync(owner, f.Channel.Id, name: new string('n', Contact.DisplayNameMaxLength + 1)), "customerName");
        await AssertValidationErrorUnderAsync(await StartConversationAsync(owner, f.Channel.Id, text: ""), "text");
        await AssertValidationErrorUnderAsync(await StartConversationAsync(owner, f.Channel.Id, text: new string('t', Message.TextMaxLength + 1)), "text");
        await AssertValidationErrorUnderAsync(await CustomerWritesAsync(owner, conversation.Id, text: null), "text");
    }

    [Fact]
    public async Task TheSimulatedCustomer_CannotReachAnotherCompany_NorARealConversation_NorAClosedOne_NorAnInactiveChannel()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        var (other, otherOwner, _) = await StartAsync();
        using var ___ = other;
        var foreignSandbox = await StartIngestedAsync(other, otherOwner);
        var real = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-1), [In(0)]);
        var closed = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-1), [In(0)], closed: true, sandbox: true);
        var inactive = await CreateInactiveChannelAsync(f.Tenant.Owner.Client, f.Branch.Id, "+57 300 555 0300");
        var eventsBefore = (await SnapshotAsync(_factory.Services, f.TenantId)).Events.Count;
        var otherEventsBefore = (await SnapshotAsync(_factory.Services, other.TenantId)).Events.Count;

        // Another company's channel and conversation are as unknown as ones that do not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await StartConversationAsync(owner, other.Channel.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await StartConversationAsync(owner, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CustomerWritesAsync(owner, foreignSandbox.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CustomerWritesAsync(owner, Guid.NewGuid())).StatusCode);

        var onReal = await CustomerWritesAsync(owner, real.ConversationId);
        var onClosed = await CustomerWritesAsync(owner, closed.ConversationId);
        var onInactive = await StartConversationAsync(owner, inactive.Id);

        Assert.Equal((HttpStatusCode.Conflict, "not_sandbox_conversation"), (onReal.StatusCode, await CodeOfAsync(onReal)));
        Assert.Equal((HttpStatusCode.Conflict, "conversation_closed"), (onClosed.StatusCode, await CodeOfAsync(onClosed)));
        Assert.Equal((HttpStatusCode.Conflict, "channel_inactive"), (onInactive.StatusCode, await CodeOfAsync(onInactive)));

        Assert.Equal(eventsBefore, (await SnapshotAsync(_factory.Services, f.TenantId)).Events.Count);
        Assert.Equal(otherEventsBefore, (await SnapshotAsync(_factory.Services, other.TenantId)).Events.Count);
    }

    [Fact]
    public async Task ASuspendedCompany_CannotUseTheSandbox_LikeTheRestOfTheApi()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{f.TenantId}/status", new { status = "Suspended" })).StatusCode);

        var response = await StartConversationAsync(owner, f.Channel.Id);

        Assert.Equal((HttpStatusCode.Forbidden, "tenant_not_active"), (response.StatusCode, await CodeOfAsync(response)));
    }

    [Fact]
    public async Task TheSimulatedCustomer_IsRateLimited_LikeSending()
    {
        var limited = _factory.GetOrAddHost("sandbox-rate-limit", () => _factory.WithWebHostBuilder(b => b.UseSetting("Messaging:SendRateLimitPerMinute", "2")));
        var (f, _, admin) = await StartAsync();
        using var __ = f;
        using var ___ = admin;
        using var owner = limited.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Tenant.Owner.Token);

        Assert.Equal(HttpStatusCode.Accepted, (await StartConversationAsync(owner, f.Channel.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await StartConversationAsync(owner, f.Channel.Id)).StatusCode);
        var third = await StartConversationAsync(owner, f.Channel.Id);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
        Assert.Equal(2, (await SnapshotAsync(_factory.Services, f.TenantId)).Events.Count);
    }

    // ---- The simulated customer: the real pipeline --------------------------------------------------------------------

    [Fact]
    public async Task AStartedConversation_EntersTheRealQueue_AndIngestionMarksEverythingItCreatesAsSandbox()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        await ClearQueueAsync(_factory.Services);

        var response = await StartConversationAsync(owner, f.Channel.Id, "Laura Gómez", "Hola, ¿cuánto cuesta la depilación?");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await ReadBodyAsync<SandboxMessageAcceptedResponse>(response);
        Assert.True(Contact.IsSandboxWaId(accepted.CustomerWaId));

        // Stored as a pending delivery of the channel, in the provider's shape; nothing else exists yet.
        var queued = await SnapshotAsync(_factory.Services, f.TenantId);
        var pending = Assert.Single(queued.Events);
        Assert.Equal((WebhookEventStatus.Pending, f.Channel.Id, true), (pending.Status, pending.ChannelId, pending.IsSandbox));
        using (var payload = JsonDocument.Parse(pending.RawPayload))
        {
            Assert.Equal("whatsapp_business_account", payload.RootElement.GetProperty("object").GetString());
        }

        Assert.Empty(queued.Conversations);

        await IngestAsync();

        var stored = await SnapshotAsync(_factory.Services, f.TenantId);
        var processed = Assert.Single(stored.Events);
        Assert.Equal((WebhookEventStatus.Processed, 1, 0), (processed.Status, processed.StoredMessageCount, processed.SkippedMessageCount));
        var contact = Assert.Single(stored.Contacts);
        Assert.Equal((accepted.CustomerWaId, "Laura Gómez", true), (contact.WaId, contact.DisplayName, contact.IsSandbox));
        var conversation = Assert.Single(stored.Conversations);
        Assert.True(conversation.IsSandbox);
        Assert.Equal((ConversationStatus.Open, f.Channel.Id, f.Branch.Id, ConversationSource.Organic), (conversation.Status, conversation.ChannelId, conversation.BranchId, conversation.Source));
        Assert.True(conversation.ClassificationDirty); // the same classification request as a real delivery
        Assert.Single(stored.Opportunities);
        var message = Assert.Single(stored.Messages);
        Assert.Equal((MessageDirection.Inbound, "text", "Hola, ¿cuánto cuesta la depilación?"), (message.Direction, message.Type, message.Text));
        Assert.StartsWith("sandbox.", message.ProviderMessageId, StringComparison.Ordinal);
        Assert.Equal(accepted.SentAt, message.SentAt);

        // The list and the detail say it is sandbox data.
        var list = await owner.GetFromJsonAsync<PagedResponse<ConversationListItem>>("/api/conversations");
        Assert.True(Assert.Single(list!.Items).IsSandbox);
        Assert.True((await owner.GetFromJsonAsync<ConversationDetailResponse>($"/api/conversations/{conversation.Id}"))!.IsSandbox);
    }

    [Fact]
    public async Task TheCustomerWritesAgain_IntoTheSameConversation_ThroughTheSamePipeline()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        var conversation = await StartIngestedAsync(f, owner);

        var response = await CustomerWritesAsync(owner, conversation.Id, "Me parece caro");
        await IngestAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.Equal(conversation.Id, Assert.Single(stored.Conversations).Id);
        Assert.Single(stored.Contacts);
        Assert.Equal(["Hola, quisiera información", "Me parece caro"], stored.Messages.Select(m => m.Text));
        Assert.All(stored.Messages, m => Assert.Equal(MessageDirection.Inbound, m.Direction));
        Assert.All(stored.Events, e => Assert.True(e.IsSandbox));
    }

    // ---- Real webhooks while the mode is on, and the wall between the two kinds of data ---------------------------------

    [Fact]
    public async Task ARealWebhook_WhileSandboxModeIsOn_IsStillIngested_AsRealData()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        var sandbox = await StartIngestedAsync(f, owner);
        var waId = UniqueWaId();

        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.{Guid.NewGuid():N}", waId: waId));
        await IngestAsync();

        var stored = await SnapshotAsync(_factory.Services, f.TenantId);
        var real = Assert.Single(stored.Conversations, c => c.Id != sandbox.Id);
        Assert.False(real.IsSandbox);
        Assert.False(Assert.Single(stored.Contacts, c => c.WaId == waId).IsSandbox);
        // One delivery of each origin: the simulated one that opened the sandbox conversation, and the webhook's, which stays real.
        Assert.Equal([true, false], stored.Events.OrderBy(e => e.ReceivedAt).Select(e => e.IsSandbox));
        Assert.Equal(real.Id, Assert.Single(stored.Messages, m => m.ProviderMessageId.StartsWith("wamid.", StringComparison.Ordinal)).ConversationId);
    }

    [Fact]
    public async Task ARealWebhook_ThatNamesASimulatedCustomersNumber_StoresNothing_AndIsCountedAsSkipped()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        var sandbox = await StartIngestedAsync(f, owner);
        var simulated = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Contacts);

        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.{Guid.NewGuid():N}", waId: simulated.WaId, name: "Suplantador"));
        await IngestAsync();

        var stored = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.Equal(sandbox.Id, Assert.Single(stored.Conversations).Id);
        Assert.Single(stored.Messages);
        Assert.Equal("Laura Gómez", Assert.Single(stored.Contacts).DisplayName);
        var real = Assert.Single(stored.Events, e => !e.IsSandbox);
        Assert.Equal((WebhookEventStatus.Processed, 0, 1), (real.Status, real.StoredMessageCount, real.SkippedMessageCount));
    }

    [Fact]
    public async Task TheWebhook_CannotMarkItsDeliveriesAsSandbox_AndItsAuthenticationIsTheSameInSandboxMode()
    {
        var (f, _, admin) = await StartAsync();
        using var __ = f;
        using var ___ = admin;
        var body = Dialog360Samples.InboundText(id: $"wamid.{Guid.NewGuid():N}", waId: UniqueWaId());

        // The secret is still required, exactly as outside sandbox mode.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync(_client, f.Path, null, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync(_client, f.Path, "wrong-secret", body)).StatusCode);

        // Nothing a caller adds to the request turns a delivery into sandbox data.
        using var request = new HttpRequestMessage(HttpMethod.Post, f.Path + "?isSandbox=true&sandbox=true")
        {
            Content = new StringContent(body.TrimEnd().TrimEnd('}') + ",\"isSandbox\":true,\"sandbox\":true}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Webhook-Secret", f.Secret);
        request.Headers.Add("X-Sandbox", "true");
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);

        Assert.False(Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Events).IsSandbox);
    }

    // ---- Sending as the advisor -------------------------------------------------------------------------------------------

    [Fact]
    public async Task InSandboxMode_AnAdvisorsMessage_IsRecordedLikeARealOne_AndNoRequestLeavesForTheProvider_EvenWithAStoredKey()
    {
        var (f, owner, admin) = await StartAsync(sandboxMode: false);
        using var _ = f;
        using var __ = admin;
        var key = NewProviderKey();
        await SetCredentialsAsync(owner, f.Channel.Id, key);

        // Outside sandbox mode the wire is live: a real conversation's message goes to (what stands in for) 360dialog, with this key.
        var real = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-1), [In(0)]);
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, real.ConversationId, "mensaje-real-por-el-proveedor")).StatusCode);
        Assert.Contains("mensaje-real-por-el-proveedor", Assert.Single(_host.Wire.WithKey(key)).Body);

        await SetSandboxModeAsync(admin, f.TenantId, true);
        var conversation = await StartIngestedAsync(f, owner);

        var response = await SendMessageAsync(owner, conversation.Id, "respuesta-del-asesor-en-sandbox");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sent = await ReadBodyAsync<MessageResponse>(response);
        Assert.Equal((nameof(MessageDirection.Outbound), "text", "respuesta-del-asesor-en-sandbox"), (sent.Direction, sent.Type, sent.Text));
        Assert.Single(_host.Wire.WithKey(key)); // still only the real one: nothing of this channel left in sandbox mode
        Assert.Equal(0, _host.Wire.CountContaining("respuesta-del-asesor-en-sandbox"));

        var stored = await SnapshotAsync(_factory.Services, f.TenantId);
        var message = Assert.Single(stored.Messages, m => m.Id == sent.Id);
        Assert.Equal((conversation.Id, MessageDirection.Outbound, f.Tenant.Owner.UserId), (message.ConversationId, message.Direction, message.CreatedBy!.Value));
        Assert.StartsWith("sandbox.", message.ProviderMessageId, StringComparison.Ordinal);
        var after = Assert.Single(stored.Conversations, c => c.Id == conversation.Id);
        Assert.NotNull(after.FirstResponseAt);
        Assert.True(after.ClassificationDirty);
    }

    [Fact]
    public async Task InSandboxMode_SendingNeedsNoProviderKey_AndNoMasterKey()
    {
        var (f, _, admin) = await StartAsync();
        using var __ = f;
        using var ___ = admin;
        var conversation = await StartIngestedAsync(f, f.Tenant.Owner.Client);
        Assert.False((await LoadChannelAsync(_factory.Services, f.Channel.Id)).HasProviderApiKey);

        // The shared host has no master key at all: the internal sender never asks for one.
        var response = await SendMessageAsync(f.Tenant.Owner.Client, conversation.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ASandboxConversation_NeverTakesARealMessage_OnceTheModeIsOff_AndARealOneTakesNoneWhileItIsOn()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        var key = NewProviderKey();
        await SetCredentialsAsync(owner, f.Channel.Id, key);
        var sandbox = await StartIngestedAsync(f, owner);
        var real = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-1), [In(0)]);
        var messagesBefore = (await SnapshotAsync(_factory.Services, f.TenantId)).Messages.Count;

        // Mode on: the real conversation is closed to the advisor, so no made-up message lands in real data.
        var onReal = await SendMessageAsync(owner, real.ConversationId, "no-debe-salir-1");
        Assert.Equal((HttpStatusCode.Conflict, "sandbox_mode_active"), (onReal.StatusCode, await CodeOfAsync(onReal)));

        // Mode off: the sandbox conversation is closed to the advisor, so nothing real is ever sent to a made-up number.
        await SetSandboxModeAsync(admin, f.TenantId, false);
        var onSandbox = await SendMessageAsync(owner, sandbox.Id, "no-debe-salir-2");
        Assert.Equal((HttpStatusCode.Conflict, "sandbox_conversation"), (onSandbox.StatusCode, await CodeOfAsync(onSandbox)));

        Assert.Empty(_host.Wire.WithKey(key));
        Assert.Equal(0, _host.Wire.CountContaining("no-debe-salir"));
        Assert.Equal(messagesBefore, (await SnapshotAsync(_factory.Services, f.TenantId)).Messages.Count);

        // And the real one sends again, through the provider.
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, real.ConversationId, "ya-sale-por-el-proveedor")).StatusCode);
        Assert.Contains("ya-sale-por-el-proveedor", Assert.Single(_host.Wire.WithKey(key)).Body);
    }

    // ---- Metrics: every query path, on each side of the wall ------------------------------------------------------------

    [Fact]
    public async Task EveryMetricsQuery_CountsRealDataOnly_OutsideSandboxMode_AndSandboxDataOnly_InsideIt()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        using var admin = await AdminAsync();
        var start = DateTime.UtcNow.AddHours(-3);
        Msg[] chat = [In(0), Out(60), In(120)];

        // Real: two conversations, one lost (too expensive), one unanswered. Sandbox: one, lost for another reason, and one unanswered.
        await SeedConversationAsync(_factory.Services, t.TenantId, t.Centro.Id, t.Fixture.Channel.Id, start, chat,
            configureOpportunity: o => MetricsTenant.Decide(o, start, OpportunityStage.Lost, LossReason.Price, ObjectionType.Price));
        await SeedConversationAsync(_factory.Services, t.TenantId, t.Centro.Id, t.Fixture.Channel.Id, start, chat);
        await SeedConversationAsync(_factory.Services, t.TenantId, t.Centro.Id, t.Fixture.Channel.Id, start, [In(0)]);
        await SeedConversationAsync(_factory.Services, t.TenantId, t.Centro.Id, t.Fixture.Channel.Id, start, [In(0), Out(30)], sandbox: true,
            configureOpportunity: o => MetricsTenant.Decide(o, start, OpportunityStage.Lost, LossReason.NoResponse, ObjectionType.Timing));
        await SeedConversationAsync(_factory.Services, t.TenantId, t.Centro.Id, t.Fixture.Channel.Id, start, [In(0)], sandbox: true);

        var real = await ReadEveryQueryAsync(t.TenantId, start);
        Assert.Equal(new QueryNumbers(Conversations: 3, Lost: 1, Answered: 2, LossReason: "Price", Objections: "None:2,Price:1", PerDay: 3, MessagesIn: 5, MessagesOut: 2, PerHour: 3, InboundPerHour: 5, Unanswered: 1), real);

        await SetSandboxModeAsync(admin, t.TenantId, true);
        var sandbox = await ReadEveryQueryAsync(t.TenantId, start);
        Assert.Equal(new QueryNumbers(Conversations: 2, Lost: 1, Answered: 1, LossReason: "NoResponse", Objections: "None:1,Timing:1", PerDay: 2, MessagesIn: 2, MessagesOut: 1, PerHour: 2, InboundPerHour: 2, Unanswered: 1), sandbox);

        await SetSandboxModeAsync(admin, t.TenantId, false);
        Assert.Equal(real, await ReadEveryQueryAsync(t.TenantId, start));
    }

    private sealed record QueryNumbers(
        int Conversations, int Lost, int Answered, string LossReason, string Objections, int PerDay, int MessagesIn, int MessagesOut, int PerHour, int InboundPerHour, int Unanswered);

    /// <summary>One number from each of the nine aggregations of <see cref="IMetricsQueries"/>, for the day around <paramref name="start"/>.</summary>
    private async Task<QueryNumbers> ReadEveryQueryAsync(Guid tenantId, DateTime start)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var queries = scope.ServiceProvider.GetRequiredService<IMetricsQueries>();
        var filter = new MetricsFilter(start.AddDays(-1), start.AddDays(1), null, null, false, null);
        var now = DateTime.UtcNow;

        var conversion = Assert.Single(await queries.ConversionAsync(filter, MetricsGroupBy.None, now));
        var response = Assert.Single(await queries.FirstResponseAsync(filter, MetricsGroupBy.None, TimeSpan.FromMinutes(30), now));
        var loss = await queries.LossReasonsAsync(filter);
        var objections = await queries.ObjectionsAsync(filter);
        var perDay = await queries.ConversationsPerDayAsync(filter, "UTC");
        var messages = await queries.MessagesPerDayAsync(filter, "UTC");
        var perHour = await queries.ConversationsPerHourOfWeekAsync(filter, "UTC");
        var inboundPerHour = await queries.InboundMessagesPerHourOfWeekAsync(filter, "UTC");
        var (unanswered, items) = await queries.UnansweredAsync(filter, now, 50);
        Assert.Equal(unanswered, items.Count);

        // The grouped variants read the same cohort.
        Assert.Equal(conversion.Total, (await queries.ConversionAsync(filter, MetricsGroupBy.Branch, now)).Sum(r => r.Total));
        Assert.Equal(conversion.Total, (await queries.ConversionAsync(filter, MetricsGroupBy.Advisor, now)).Sum(r => r.Total));
        Assert.Equal(conversion.Total, (await queries.ConversionAsync(filter, MetricsGroupBy.Source, now)).Sum(r => r.Total));
        Assert.Equal(response.Conversations, (await queries.FirstResponseAsync(filter, MetricsGroupBy.Branch, TimeSpan.FromMinutes(30), now)).Sum(r => r.Conversations));

        return new QueryNumbers(
            conversion.Total,
            conversion.Lost,
            response.Answered,
            string.Join(",", loss.OrderBy(l => l.Reason).Select(l => l.Reason)),
            string.Join(",", objections.OrderBy(o => o.Objection).Select(o => $"{o.Objection}:{o.Count}")),
            perDay.Sum(d => d.Count),
            messages.Sum(d => d.Inbound),
            messages.Sum(d => d.Outbound),
            perHour.Sum(h => h.Count),
            inboundPerHour.Sum(h => h.Count),
            unanswered);
    }

    [Fact]
    public async Task EveryMetricsEndpoint_FollowsTheMode_AndAnotherCompanysModeChangesNothing()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        using var other = await MetricsTenant.StartAsync(_factory, _client);
        using var admin = await AdminAsync();
        var start = DateTime.UtcNow.AddHours(-3);
        await t.SeedAsync(_factory.Services, false, start, [In(0), Out(60)], stage: OpportunityStage.Won);
        await t.SeedAsync(_factory.Services, false, start, [In(0), Out(60)], stage: OpportunityStage.Lost, loss: LossReason.Price);
        await SeedConversationAsync(_factory.Services, t.TenantId, t.Centro.Id, t.Fixture.Channel.Id, start, [In(0)], sandbox: true);
        await other.SeedAsync(_factory.Services, false, start, [In(0), Out(60)], stage: OpportunityStage.Won);

        async Task<(int Summary, int Conversion, int FirstResponse, int Lost, int Volume, int Unanswered)> ReadAsync(HttpClient client)
        {
            async Task<JsonElement> Get(string endpoint)
            {
                using var json = JsonDocument.Parse(await client.GetStringAsync($"/api/metrics/{endpoint}"));
                return json.RootElement.Clone();
            }

            var alerts = (await Get("alerts")).GetProperty("unanswered");
            return (
                (await Get("summary")).GetProperty("conversion").GetProperty("total").GetInt32(),
                (await Get("conversion")).GetProperty("totals").GetProperty("total").GetInt32(),
                (await Get("first-response")).GetProperty("totals").GetProperty("conversations").GetInt32(),
                (await Get("loss-reasons")).GetProperty("totalLost").GetInt32(),
                (await Get("volume")).GetProperty("conversations").GetInt32(),
                alerts.GetProperty("total").GetInt32());
        }

        Assert.Equal((2, 2, 2, 1, 2, 0), await ReadAsync(t.Owner));

        await SetSandboxModeAsync(admin, t.TenantId, true);
        Assert.Equal((1, 1, 1, 0, 1, 1), await ReadAsync(t.Owner));
        Assert.Equal((1, 1, 1, 0, 1, 0), await ReadAsync(other.Owner)); // the other company still sees its own real data

        await SetSandboxModeAsync(admin, t.TenantId, false);
        Assert.Equal((2, 2, 2, 1, 2, 0), await ReadAsync(t.Owner));
    }

    // ---- Deleting the sandbox data ----------------------------------------------------------------------------------------

    [Fact]
    public async Task DeletingTheSandboxData_RemovesOnlyWhatIsMarkedSandbox_OfThisCompanyOnly()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        var (other, otherOwner, _) = await StartAsync();
        using var ___ = other;

        // This company: a real conversation that came through the webhook, a seeded real one, and two simulated ones (one answered).
        await ClearQueueAsync(_factory.Services);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.{Guid.NewGuid():N}", waId: UniqueWaId()));
        await IngestAsync();
        var real = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0), Out(30)]);
        var first = await StartIngestedAsync(f, owner);
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, first.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await CustomerWritesAsync(owner, first.Id)).StatusCode);
        await IngestAsync();
        await StartIngestedAsync(f, owner);
        // A simulated delivery still waiting in the queue goes too, so it cannot come back as a conversation afterwards.
        Assert.Equal(HttpStatusCode.Accepted, (await StartConversationAsync(owner, f.Channel.Id)).StatusCode);

        // The other company: its own sandbox data, which must survive.
        await StartIngestedAsync(other, otherOwner);
        var otherBefore = await SnapshotAsync(_factory.Services, other.TenantId);

        var before = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.Equal((4, 2, 4), (before.Conversations.Count, before.Conversations.Count(c => c.IsSandbox), before.Events.Count(e => e.IsSandbox)));

        var response = await owner.DeleteAsync(DataUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var deleted = await ReadBodyAsync<SandboxDataDeletedResponse>(response);
        Assert.Equal((2, 4, 2, 4), (deleted.Conversations, deleted.Messages, deleted.Contacts, deleted.WebhookEvents));

        var after = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.DoesNotContain(after.Conversations, c => c.IsSandbox);
        Assert.DoesNotContain(after.Contacts, c => c.IsSandbox);
        Assert.DoesNotContain(after.Events, e => e.IsSandbox);
        Assert.Equal(before.Conversations.Where(c => !c.IsSandbox).Select(c => c.Id).Order(), after.Conversations.Select(c => c.Id).Order());
        Assert.Equal(before.Contacts.Where(c => !c.IsSandbox).Select(c => c.Id).Order(), after.Contacts.Select(c => c.Id).Order());
        Assert.Equal(before.Events.Where(e => !e.IsSandbox).Select(e => e.Id).Order(), after.Events.Select(e => e.Id).Order());
        var realConversations = after.Conversations.Select(c => c.Id).ToHashSet();
        Assert.Equal(before.Messages.Where(m => realConversations.Contains(m.ConversationId)).Select(m => m.Id).Order(), after.Messages.Select(m => m.Id).Order());
        Assert.Equal(before.Opportunities.Where(o => realConversations.Contains(o.ConversationId)).Select(o => o.Id).Order(), after.Opportunities.Select(o => o.Id).Order());
        Assert.Contains(after.Conversations, c => c.Id == real.ConversationId);

        // Nothing waits in the queue to bring sandbox rows back.
        await IngestAsync();
        Assert.DoesNotContain((await SnapshotAsync(_factory.Services, f.TenantId)).Conversations, c => c.IsSandbox);

        // The other company is untouched.
        var otherAfter = await SnapshotAsync(_factory.Services, other.TenantId);
        Assert.Equal(
            (otherBefore.Conversations.Count, otherBefore.Messages.Count, otherBefore.Contacts.Count, otherBefore.Events.Count, otherBefore.Opportunities.Count),
            (otherAfter.Conversations.Count, otherAfter.Messages.Count, otherAfter.Contacts.Count, otherAfter.Events.Count, otherAfter.Opportunities.Count));
        Assert.All(otherAfter.Conversations, c => Assert.True(c.IsSandbox));

        // Deleting again finds nothing, and it also works once the mode is off.
        await SetSandboxModeAsync(admin, f.TenantId, false);
        var again = await ReadBodyAsync<SandboxDataDeletedResponse>(await owner.DeleteAsync(DataUrl));
        Assert.Equal((0, 0, 0, 0), (again.Conversations, again.Messages, again.Contacts, again.WebhookEvents));
    }

    // ---- End to end ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EndToEnd_OnePersonPlaysBothSides_AndTheConversationIsClassifiedCountedHiddenAndDeleted()
    {
        var (f, owner, admin) = await StartAsync(sandboxMode: false);
        using var _ = f;
        using var __ = admin;
        var key = NewProviderKey();
        await SetCredentialsAsync(owner, f.Channel.Id, key); // a key is stored, and must never be used below
        var real = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddHours(-2), [In(0), Out(60)]);

        async Task<(int Conversations, int Lost, int MessagesIn, int MessagesOut)> MetricsAsync()
        {
            using var conversion = JsonDocument.Parse(await owner.GetStringAsync("/api/metrics/conversion"));
            using var volume = JsonDocument.Parse(await owner.GetStringAsync("/api/metrics/volume"));
            var totals = conversion.RootElement.GetProperty("totals");
            return (totals.GetProperty("total").GetInt32(), totals.GetProperty("lost").GetInt32(),
                volume.RootElement.GetProperty("inboundMessages").GetInt32(), volume.RootElement.GetProperty("outboundMessages").GetInt32());
        }

        var realNumbers = await MetricsAsync();
        Assert.Equal((1, 0, 1, 1), realNumbers);

        // 1. The administrator switches the sandbox on: the dashboard is empty (real data is hidden, not gone).
        await SetSandboxModeAsync(admin, f.TenantId, true, "Prueba de punta a punta");
        Assert.Equal((0, 0, 0, 0), await MetricsAsync());

        // 2. A simulated customer writes; ingestion runs.
        var conversation = await StartIngestedAsync(f, owner, "Hola, cuánto cuesta la depilación de axilas?");
        Assert.True(conversation.IsSandbox);

        // 3. The advisor answers from the same screen; 4. the customer declines.
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, conversation.Id, "Son $60.000 la sesión.")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await CustomerWritesAsync(owner, conversation.Id, "No gracias")).StatusCode);
        await IngestAsync();

        var detail = (await owner.GetFromJsonAsync<ConversationDetailResponse>($"/api/conversations/{conversation.Id}"))!;
        Assert.Equal(
            [("Inbound", "Hola, cuánto cuesta la depilación de axilas?"), ("Outbound", "Son $60.000 la sesión."), ("Inbound", "No gracias")],
            detail.Messages.Select(m => (m.Direction, m.Text!)));
        Assert.True(detail.IsSandbox);
        Assert.NotNull(detail.FirstResponseAt);

        // 5. The classifier (the real worker's runner, with no quiet period) reads it like any conversation.
        var classifier = new ConversationClassificationRunner(
            _host.Host.Services.GetRequiredService<IServiceScopeFactory>(), ClassificationOptions.Default with { DebounceSeconds = 0 },
            new FixedTimeProvider(DateTime.UtcNow.AddMinutes(1)), NullLogger<ConversationClassificationRunner>.Instance);
        while (await classifier.ProcessNextAsync())
        {
        }

        var (classified, opportunity) = await LoadAsync(_factory.Services, conversation.Id);
        Assert.False(classified.ClassificationDirty);
        Assert.NotNull(opportunity.ClassifiedAt);
        Assert.Equal((OpportunityStage.Lost, StageSource.Auto), (opportunity.Stage, opportunity.StageSource));

        // 6. The metrics count it, and only it.
        Assert.Equal((1, 1, 2, 1), await MetricsAsync());

        // 7. The mode goes off: the same metrics are the real ones again, and the sandbox conversation is still listed, marked.
        await SetSandboxModeAsync(admin, f.TenantId, false, "Fin de la prueba");
        Assert.Equal(realNumbers, await MetricsAsync());
        var list = await owner.GetFromJsonAsync<PagedResponse<ConversationListItem>>("/api/conversations");
        Assert.Equal([(real.ConversationId, false), (conversation.Id, true)], list!.Items.Select(c => (c.Id, c.IsSandbox)).OrderBy(c => c.IsSandbox));

        // 8. The clean-up removes the sandbox rows and nothing else.
        var deleted = await ReadBodyAsync<SandboxDataDeletedResponse>(await owner.DeleteAsync(DataUrl));
        Assert.Equal((1, 3, 1, 2), (deleted.Conversations, deleted.Messages, deleted.Contacts, deleted.WebhookEvents));
        var after = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.Equal(real.ConversationId, Assert.Single(after.Conversations).Id);
        Assert.Equal(2, after.Messages.Count);
        Assert.Single(after.Contacts);
        Assert.Single(after.Opportunities);
        Assert.Equal(realNumbers, await MetricsAsync());

        // Not one request went to the provider in the whole exercise.
        Assert.Empty(_host.Wire.WithKey(key));
    }
}
