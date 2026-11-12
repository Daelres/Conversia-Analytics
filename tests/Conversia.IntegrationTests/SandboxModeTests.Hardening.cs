using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Messages;
using Conversia.Application.DTOs.Metrics;
using Conversia.Application.DTOs.Sandbox;
using Conversia.Application.Interfaces;
using Conversia.Application.Ingestion;
using Conversia.Application.Messaging;
using Conversia.Application.Metrics;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Conversations;
using Conversia.Infrastructure.Messaging;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The edges of the sandbox mode (docs/SANDBOX.md): the diagnostic views and the administrator's summary, the log trail, what happens
/// when the clean-up races a send or the ingestion worker, what the inactivity job does with each kind of conversation, and the lists and
/// metrics over mixed data in both modes.
/// </summary>
public partial class SandboxModeTests
{
    // ---- 1a. The diagnostic views show both kinds, each marked -------------------------------------------------------------

    [Fact]
    public async Task TheMessagesFeed_AndTheDeliveries_ShowBothKinds_EachMarkedFromItsConversationOrDelivery()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        var sandbox = await StartIngestedAsync(f, owner, "mensaje-del-cliente-simulado");
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, sandbox.Id, "respuesta-en-sandbox")).StatusCode);
        await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.{Guid.NewGuid():N}", waId: UniqueWaId(), text: "mensaje-real-del-webhook"));
        await IngestAsync();

        foreach (var mode in new[] { true, false })
        {
            await SetSandboxModeAsync(admin, f.TenantId, mode);

            var messages = (await owner.GetFromJsonAsync<PagedResponse<RecentMessageItem>>("/api/messages/recent"))!;
            Assert.Equal(3, messages.TotalCount);
            Assert.Equal(
                [("mensaje-del-cliente-simulado", true), ("mensaje-real-del-webhook", false), ("respuesta-en-sandbox", true)],
                messages.Items.Select(m => (m.Text!, m.IsSandbox)).OrderBy(m => m.Item1, StringComparer.Ordinal));

            var deliveries = (await owner.GetFromJsonAsync<PagedResponse<WebhookEventItem>>("/api/webhook-events"))!;
            Assert.Equal(2, deliveries.TotalCount);
            Assert.Equal([false, true], deliveries.Items.Select(d => d.IsSandbox).Order());
            var stored = (await SnapshotAsync(_factory.Services, f.TenantId)).Events.ToDictionary(e => e.Id, e => e.IsSandbox);
            Assert.All(deliveries.Items, d => Assert.Equal(stored[d.Id], d.IsSandbox));

            // The filters of the feed do not lose the mark.
            var inbound = (await owner.GetFromJsonAsync<PagedResponse<RecentMessageItem>>("/api/messages/recent?direction=Inbound&pageSize=1&page=2"))!;
            Assert.Equal(2, inbound.TotalCount);
            Assert.Single(inbound.Items);
        }
    }

    // ---- 1b. The administrator's "last activity" ignores sandbox data --------------------------------------------------------

    [Fact]
    public async Task TheLastActivityTheAdministratorSees_IsTheLastRealMessage_NeverASimulatedOne()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;

        // Only sandbox messages so far: the company has had no activity.
        var sandbox = await StartIngestedAsync(f, owner);
        Assert.Equal(HttpStatusCode.Created, (await SendMessageAsync(owner, sandbox.Id)).StatusCode);
        Assert.Null((await GetAdminTenantAsync(admin, f.TenantId)).LastActivityAt);
        var name = (await GetAdminTenantAsync(admin, f.TenantId)).Name;
        var listed = await admin.Client.GetFromJsonAsync<PagedResponse<TenantSummaryResponse>>($"{AdminTenantsUrl}?search={Uri.EscapeDataString(name)}");
        Assert.Null(Assert.Single(listed!.Items, t => t.Id == f.TenantId).LastActivityAt);

        // A real message from two days ago is the last activity, although the sandbox ones are newer.
        var realAt = DateTime.UtcNow.AddDays(-2);
        await SeedConversationAsync(_factory.Services, f, realAt, [In(0)]);

        var detail = await GetAdminTenantAsync(admin, f.TenantId);
        Assert.InRange(detail.LastActivityAt!.Value, realAt.AddSeconds(-2), realAt.AddSeconds(2));
        await SetSandboxModeAsync(admin, f.TenantId, false);
        Assert.InRange((await GetAdminTenantAsync(admin, f.TenantId)).LastActivityAt!.Value, realAt.AddSeconds(-2), realAt.AddSeconds(2));
    }

    // ---- 3. A delivery that crosses the wall leaves a warning with ids only ---------------------------------------------------

    [Theory]
    [InlineData(true)] // a sandbox delivery that names a real customer's number
    [InlineData(false)] // a real delivery that names a simulated customer's number
    public async Task ADeliveryThatCrossesTheSandboxWall_StoresNothing_AndLeavesOneWarningWithIdsOnly(bool sandboxDelivery)
    {
        var capture = new CapturingLoggerProvider();
        using var host = WebhookHost(_factory, new(), configureLogging: logging => logging.AddProvider(capture));
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        await ClearQueueAsync(_factory.Services);
        const string privateText = "texto-privado-que-no-debe-estar-en-el-registro";

        string waId;
        if (sandboxDelivery)
        {
            // The endpoints can never produce this (they make up the number): the delivery is put in the queue directly, as a defect would.
            waId = UniqueWaId();
            await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)], waId: waId);
            using var scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(f.TenantId);
            var body = Dialog360Samples.InboundText(id: $"sandbox.in.{Guid.NewGuid():N}", waId: waId, text: privateText, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var result = await scope.ServiceProvider.GetRequiredService<IWebhookIngestionService>()
                .StoreAsync(new WebhookChannelAccess(f.Channel.Id, f.TenantId, IsSandbox: true), Encoding.UTF8.GetBytes(body));
            Assert.Equal(WebhookStoreResult.Stored, result);
        }
        else
        {
            await StartIngestedAsync(f, owner);
            waId = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Contacts).WaId;
            await DeliverAsync(_client, f, Dialog360Samples.InboundText(id: $"wamid.{Guid.NewGuid():N}", waId: waId, text: privateText, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }

        var before = await SnapshotAsync(_factory.Services, f.TenantId);
        await DrainAsync(RunnerOf(host.Services));

        var after = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.Equal(before.Messages.Count, after.Messages.Count);
        Assert.Equal(before.Conversations.Count, after.Conversations.Count);
        var crossing = Assert.Single(after.Events, e => e.IsSandbox == sandboxDelivery && e.SkippedMessageCount == 1);
        Assert.Equal((WebhookEventStatus.Processed, 0), (crossing.Status, crossing.StoredMessageCount));

        var warning = Assert.Single(capture.Entries, e => e.Category.EndsWith(nameof(WebhookEventProcessor), StringComparison.Ordinal) && e.Text.Contains(crossing.Id.ToString(), StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(f.TenantId.ToString(), warning.Text);
        Assert.Contains(f.Channel.Id.ToString(), warning.Text);
        Assert.Contains(after.Contacts.Single(c => c.WaId == waId).Id.ToString(), warning.Text);
        Assert.DoesNotContain(waId, warning.Text);
        Assert.DoesNotContain(privateText, warning.Text);
        Assert.DoesNotContain(capture.Entries, e => e.Text.Contains(privateText, StringComparison.Ordinal) || e.Text.Contains(waId, StringComparison.Ordinal));
    }

    // ---- 4. A send that loses the race with the clean-up ------------------------------------------------------------------

    /// <summary>The real resolver's choice, except that in sandbox mode the company's sandbox data is deleted while the message is "on its way".</summary>
    private sealed class DeletingMidSendResolver(IServiceScopeFactory scopes) : IWhatsAppMessageSenderResolver, IWhatsAppMessageSender
    {
        private readonly SandboxMessageSender _sandbox = new();

        public IWhatsAppMessageSender Resolve(WhatsAppChannel channel, bool sandboxMode)
            => sandboxMode ? this : throw new InvalidOperationException("Only sandbox sends are expected on this host.");

        public async Task<WhatsAppSendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken = default)
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISandboxDataStore>().DeleteAllAsync(message.Channel.TenantId, CancellationToken.None);
            return await _sandbox.SendTextAsync(message, cancellationToken);
        }
    }

    [Fact]
    public async Task ASandboxMessage_ThatLosesTheRaceWithTheCleanUp_Is409WithItsCode_NotA500_AndLeavesNothingBehind()
    {
        var racing = _factory.GetOrAddHost("sandbox-delete-mid-send", () => _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWhatsAppMessageSenderResolver>();
            services.AddSingleton<IWhatsAppMessageSenderResolver>(sp => new DeletingMidSendResolver(sp.GetRequiredService<IServiceScopeFactory>()));
        })));
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        var conversation = await StartIngestedAsync(f, owner);
        var real = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)]);
        using var client = racing.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.Tenant.Owner.Token);

        var response = await SendMessageAsync(client, conversation.Id, "llega-tarde");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var (code, body) = await ReadProblemCodeAsync(response);
        Assert.Equal("sandbox_data_deleted", code);
        Assert.DoesNotContain("errorId", body);
        Assert.False(response.Headers.Contains("X-Error-Id"));
        var after = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.Equal(real.ConversationId, Assert.Single(after.Conversations).Id);
        Assert.Single(after.Messages);
        Assert.DoesNotContain(after.Messages, m => m.Text == "llega-tarde");
    }

    // ---- 5. Open conversations when the mode changes ------------------------------------------------------------------------

    private async Task CloseIdleAsync(DateTime now)
    {
        using var scope = _factory.Services.CreateScope();
        var closer = new ConversationAutoCloser(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), new ConversationLifecycleOptions(true, 7, 15, 200));
        while ((await closer.CloseBatchAsync(now)).Closed > 0)
        {
        }
    }

    private async Task<(int Total, int Lost, int UnansweredOpen, int UnansweredClosed)> CountsAsync(HttpClient client)
    {
        var conversion = (await client.GetFromJsonAsync<ConversionResponse>("/api/metrics/conversion"))!;
        var response = (await client.GetFromJsonAsync<FirstResponseResponse>("/api/metrics/first-response"))!;
        return (conversion.Totals.Total, conversion.Totals.Lost, response.Totals.UnansweredOpen, response.Totals.UnansweredClosed);
    }

    [Fact]
    public async Task OpenConversationsOfTheHiddenKind_AreStillClosedByInactivity_AndStayOnTheirSideOfTheMetrics()
    {
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        var idleSince = DateTime.UtcNow.AddDays(-10);

        // Left open when the mode goes off: a sandbox conversation the simulated customer never answered again, idle for ten days.
        var sandbox = await SeedConversationAsync(_factory.Services, f, idleSince, [In(0), Out(60)], sandbox: true);
        await SetSandboxModeAsync(admin, f.TenantId, false);
        Assert.Equal((0, 0, 0, 0), await CountsAsync(owner));

        await CloseIdleAsync(DateTime.UtcNow);

        var (closedSandbox, sandboxOpportunity) = await LoadAsync(_factory.Services, sandbox.ConversationId);
        Assert.Equal(ConversationStatus.Closed, closedSandbox.Status);
        Assert.True(closedSandbox.IsSandbox);
        Assert.Equal((OpportunityStage.Lost, LossReason.NoResponse), (sandboxOpportunity.Stage, sandboxOpportunity.LossReason));
        Assert.Equal((0, 0, 0, 0), await CountsAsync(owner)); // closed, lost, and still not one of the company's real numbers

        // Left open when the mode goes on: a real conversation, idle for ten days. It is closed all the same, and hidden meanwhile.
        var real = await SeedConversationAsync(_factory.Services, f, idleSince, [In(0), Out(60)]);
        await SetSandboxModeAsync(admin, f.TenantId, true);
        Assert.Equal((1, 1, 0, 0), await CountsAsync(owner)); // the sandbox one, as the job left it

        await CloseIdleAsync(DateTime.UtcNow);

        var (closedReal, _) = await LoadAsync(_factory.Services, real.ConversationId);
        Assert.Equal(ConversationStatus.Closed, closedReal.Status);
        Assert.False(closedReal.IsSandbox);
        Assert.Equal((1, 1, 0, 0), await CountsAsync(owner));
        Assert.Equal((OpportunityStage.Lost, LossReason.NoResponse), ((await LoadAsync(_factory.Services, real.ConversationId)).Opportunity.Stage, (await LoadAsync(_factory.Services, real.ConversationId)).Opportunity.LossReason));

        await SetSandboxModeAsync(admin, f.TenantId, false);
        Assert.Equal((1, 1, 0, 0), await CountsAsync(owner)); // now the real one, alone
        var list = (await owner.GetFromJsonAsync<PagedResponse<ConversationListItem>>("/api/conversations?status=Closed"))!;
        Assert.Equal([(real.ConversationId, false), (sandbox.ConversationId, true)], list.Items.Select(c => (c.Id, c.IsSandbox)).OrderBy(c => c.IsSandbox));
    }

    // ---- 6. The list of conversations over mixed data ------------------------------------------------------------------------

    [Fact]
    public async Task TheConversationList_PagesAndFiltersOverBothKinds_TheSameInBothModes()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        using var admin = await AdminAsync();
        var start = DateTime.UtcNow.AddHours(-5);
        var ids = new List<(Guid Id, bool Sandbox)>();
        async Task Seed(int minute, bool sandbox, bool north, Msg[] messages, ConversationSource source = ConversationSource.Organic, bool closed = false)
        {
            var seeded = await SeedConversationAsync(
                _factory.Services, t.TenantId, north ? t.Second.Id : t.Centro.Id, north ? t.SecondChannel.Id : t.Fixture.Channel.Id, start.AddMinutes(minute), messages,
                source, closed: closed, sandbox: sandbox);
            ids.Add((seeded.ConversationId, sandbox));
        }

        await Seed(0, sandbox: false, north: false, [In(0), Out(30)]);
        await Seed(1, sandbox: true, north: false, [In(0)]);
        await Seed(2, sandbox: false, north: true, [In(0)], ConversationSource.Ads);
        await Seed(3, sandbox: true, north: true, [In(0), Out(30)], ConversationSource.Ads);
        await Seed(4, sandbox: true, north: false, [In(0)], closed: true);
        await Seed(5, sandbox: false, north: false, [In(0)], closed: true);
        await Seed(6, sandbox: true, north: false, [In(0), Out(30)]);

        async Task<List<(Guid Id, bool Sandbox)>> ListAsync(string query)
        {
            var page = (await t.Owner.GetFromJsonAsync<PagedResponse<ConversationListItem>>($"/api/conversations?{query}"))!;
            return page.Items.Select(c => (c.Id, c.IsSandbox)).ToList();
        }

        var newestFirst = Enumerable.Reverse(ids).ToList();
        foreach (var mode in new[] { false, true, false })
        {
            await SetSandboxModeAsync(admin, t.TenantId, mode);

            // Paging: seven rows in three pages of three, newest first, each with its own mark, none lost and none twice.
            var all = (await t.Owner.GetFromJsonAsync<PagedResponse<ConversationListItem>>("/api/conversations?pageSize=3"))!;
            Assert.Equal(7, all.TotalCount);
            var paged = new List<(Guid, bool)>();
            foreach (var page in new[] { 1, 2, 3 })
            {
                paged.AddRange(await ListAsync($"pageSize=3&page={page}"));
            }

            Assert.Equal(newestFirst, paged);
            Assert.Empty(await ListAsync("pageSize=3&page=4"));

            // Filters: every one of them selects over both kinds.
            Assert.Equal(newestFirst.Where((_, i) => new[] { 3, 4 }.Contains(i)), await ListAsync($"branchId={t.Second.Id}")); // minutes 3 and 2
            Assert.Equal([(ids[3].Id, true), (ids[2].Id, false)], await ListAsync("source=Ads"));
            Assert.Equal([(ids[5].Id, false), (ids[4].Id, true)], await ListAsync("status=Closed"));
            Assert.Equal([(ids[2].Id, false), (ids[1].Id, true)], await ListAsync("unanswered=true"));
            Assert.Equal([(ids[5].Id, false), (ids[4].Id, true)], await ListAsync("closedUnanswered=true"));
            Assert.Equal([(ids[1].Id, true)], await ListAsync($"branchId={t.Centro.Id}&unanswered=true&status=Open"));
            Assert.Equal([(ids[3].Id, true)], await ListAsync($"branchId={t.Second.Id}&source=Ads&pageSize=1&page=1"));
            Assert.Equal([(ids[2].Id, false)], await ListAsync($"branchId={t.Second.Id}&source=Ads&pageSize=1&page=2"));
        }
    }

    // ---- 6. The clean-up racing the ingestion worker ------------------------------------------------------------------------

    /// <summary>The real processor, held inside the worker's transaction (tenant lock taken) until the test lets it go on.</summary>
    private sealed class GatedProcessor(WebhookEventProcessor inner, ProcessingGate gate) : IWebhookEventProcessor
    {
        public async Task<WebhookProcessingResult> ProcessAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default)
        {
            var result = await inner.ProcessAsync(webhookEvent, cancellationToken);
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    private sealed class ProcessingGate
    {
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Reset()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private static void AssertNoSandboxRowsNorOrphans(Snapshot snapshot)
    {
        Assert.DoesNotContain(snapshot.Conversations, c => c.IsSandbox);
        Assert.DoesNotContain(snapshot.Contacts, c => c.IsSandbox);
        Assert.DoesNotContain(snapshot.Events, e => e.IsSandbox);
        var conversations = snapshot.Conversations.Select(c => c.Id).ToHashSet();
        var contacts = snapshot.Contacts.Select(c => c.Id).ToHashSet();
        Assert.All(snapshot.Messages, m => Assert.Contains(m.ConversationId, conversations));
        Assert.All(snapshot.Opportunities, o => Assert.Contains(o.ConversationId, conversations));
        Assert.All(snapshot.Conversations, c => Assert.Contains(c.ContactId, contacts));
        Assert.DoesNotContain(snapshot.Messages, m => m.ProviderMessageId.StartsWith("sandbox.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCleanUp_WaitsForADeliveryThatIsBeingProcessed_ThenRemovesWhatItCreated_AndBothFinish()
    {
        var gate = new ProcessingGate();
        using var host = WebhookHost(_factory, new(), services =>
        {
            services.AddScoped<WebhookEventProcessor>();
            services.AddSingleton(gate);
            services.AddScoped<IWebhookEventProcessor, GatedProcessor>();
        });
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        await ClearQueueAsync(_factory.Services);
        var real = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddMinutes(-5), [In(0)]);
        Assert.Equal(HttpStatusCode.Accepted, (await StartConversationAsync(owner, f.Channel.Id)).StatusCode);

        // The worker is in the middle of the delivery: its rows are written, its transaction and the tenant lock are still held.
        var working = RunnerOf(host.Services).ProcessNextAsync();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var deleting = owner.DeleteAsync(DataUrl);
        await Task.Delay(400);
        Assert.False(deleting.IsCompleted); // it waits for the worker instead of deleting around it

        gate.Release.TrySetResult();
        Assert.True(await working.WaitAsync(TimeSpan.FromSeconds(30)));
        var response = await deleting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var deleted = await ReadBodyAsync<SandboxDataDeletedResponse>(response);
        Assert.Equal((1, 1, 1, 1), (deleted.Conversations, deleted.Messages, deleted.Contacts, deleted.WebhookEvents));
        var after = await SnapshotAsync(_factory.Services, f.TenantId);
        AssertNoSandboxRowsNorOrphans(after);
        Assert.Equal(real.ConversationId, Assert.Single(after.Conversations).Id);
    }

    [Fact]
    public async Task ADeliveryTheWorkerHadClaimed_ThatTheCleanUpDeletesFirst_IsDroppedQuietly_NotProcessedAndNotAFailure()
    {
        var capture = new CapturingLoggerProvider();
        using var host = WebhookHost(_factory, new(), configureLogging: logging => logging.AddProvider(capture));
        var (f, owner, admin) = await StartAsync();
        using var _ = f;
        using var __ = admin;
        await ClearQueueAsync(_factory.Services);
        Assert.Equal(HttpStatusCode.Accepted, (await StartConversationAsync(owner, f.Channel.Id)).StatusCode);
        var pending = Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Events);

        // The tenant's ingestion lock is taken here, the way another transaction of the tenant would hold it...
        await using var holder = new NpgsqlConnection(_factory.ServerConnectionString);
        await holder.OpenAsync();
        await using var holding = await holder.BeginTransactionAsync();
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", holder, holding))
        {
            take.Parameters.AddWithValue("key", f.TenantId.ToString());
            await take.ExecuteNonQueryAsync();
        }

        // ...so the worker claims the delivery and waits for the lock, and the clean-up queues for it as well.
        var working = RunnerOf(host.Services).ProcessNextAsync();
        for (var i = 0; i < 300 && (await SnapshotAsync(_factory.Services, f.TenantId)).Events.Single().Attempts == 0; i++)
        {
            await Task.Delay(20); // until the claim is committed (the attempt is counted when the delivery is claimed)
        }

        Assert.Equal(1, (await SnapshotAsync(_factory.Services, f.TenantId)).Events.Single().Attempts);
        var deleting = owner.DeleteAsync(DataUrl);
        await Task.Delay(400);
        Assert.False(deleting.IsCompleted);
        Assert.False(working.IsCompleted);

        await holding.CommitAsync();
        var response = await deleting.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(await working.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await DrainAsync(RunnerOf(host.Services));
        var after = await SnapshotAsync(_factory.Services, f.TenantId);
        AssertNoSandboxRowsNorOrphans(after);
        Assert.Empty(after.Conversations);

        // Whichever of the two got the lock first, nothing is reported as a failed delivery.
        Assert.DoesNotContain(capture.Entries, e => e.Level >= LogLevel.Warning && e.Text.Contains(pending.Id.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Entries, e => e.Level >= LogLevel.Error && e.Category.Contains("Webhook", StringComparison.Ordinal));
    }

    // ---- 6. Metrics: filters and alerts over mixed data ------------------------------------------------------------------------

    [Fact]
    public async Task TheMetricsFilters_ByBranchAdvisorAndSource_CombineWithTheSandboxWall_InBothModes()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        using var admin = await AdminAsync();
        var start = DateTime.UtcNow.AddHours(-4);
        Task Seed(bool sandbox, bool north, Guid? advisor, ConversationSource source, params Msg[] messages)
            => SeedConversationAsync(
                _factory.Services, t.TenantId, north ? t.Second.Id : t.Centro.Id, north ? t.SecondChannel.Id : t.Fixture.Channel.Id, start, messages,
                source, advisor, sandbox: sandbox);

        // Real: Centro/Ana/Ads (2 messages), Norte/Beto/Organic (1).  Sandbox: Centro/Ana/Ads x2 (3 messages each), Centro/unassigned/Organic (1), Norte/Beto/Ads (1).
        await Seed(false, false, t.Ana.Id, ConversationSource.Ads, In(0), Out(30));
        await Seed(false, true, t.Beto.Id, ConversationSource.Organic, In(0));
        await Seed(true, false, t.Ana.Id, ConversationSource.Ads, In(0), Out(30), In(60));
        await Seed(true, false, t.Ana.Id, ConversationSource.Ads, In(0), Out(30), In(60));
        await Seed(true, false, null, ConversationSource.Organic, In(0));
        await Seed(true, true, t.Beto.Id, ConversationSource.Ads, In(0));

        async Task<(int Conversations, int Answered, int Messages, int Unanswered)> ReadAsync(string query)
        {
            var conversion = (await t.Owner.GetFromJsonAsync<ConversionResponse>($"/api/metrics/conversion?{query}"))!;
            var response = (await t.Owner.GetFromJsonAsync<FirstResponseResponse>($"/api/metrics/first-response?{query}"))!;
            var volume = (await t.Owner.GetFromJsonAsync<VolumeResponse>($"/api/metrics/volume?{query}"))!;
            var alerts = (await t.Owner.GetFromJsonAsync<AlertsResponse>($"/api/metrics/alerts?{query}"))!;
            Assert.Equal(conversion.Totals.Total, volume.Conversations);
            Assert.Equal(conversion.Totals.Total, conversion.ByBranch.Sum(b => b.Total));
            Assert.Equal(conversion.Totals.Total, conversion.ByAdvisor.Sum(b => b.Total));
            Assert.Equal(conversion.Totals.Total, conversion.BySource.Sum(b => b.Total));
            return (conversion.Totals.Total, response.Totals.Answered, volume.InboundMessages + volume.OutboundMessages, alerts.Unanswered.Total);
        }

        var centro = $"branchId={t.Centro.Id}";
        var north = $"branchId={t.Second.Id}";
        var ana = $"advisorId={t.Ana.Id}";
        var beto = $"advisorId={t.Beto.Id}";

        async Task AssertRealAsync()
        {
            Assert.Equal((2, 1, 3, 1), await ReadAsync(string.Empty));
            Assert.Equal((1, 1, 2, 0), await ReadAsync(centro));
            Assert.Equal((1, 0, 1, 1), await ReadAsync(north));
            Assert.Equal((1, 1, 2, 0), await ReadAsync(ana));
            Assert.Equal((1, 0, 1, 1), await ReadAsync(beto));
            Assert.Equal((0, 0, 0, 0), await ReadAsync("unassigned=true"));
            Assert.Equal((1, 1, 2, 0), await ReadAsync("source=Ads"));
            Assert.Equal((1, 0, 1, 1), await ReadAsync("source=Organic"));
            Assert.Equal((1, 1, 2, 0), await ReadAsync($"{centro}&{ana}&source=Ads"));
            Assert.Equal((0, 0, 0, 0), await ReadAsync($"{north}&source=Ads"));
        }

        await AssertRealAsync();

        await SetSandboxModeAsync(admin, t.TenantId, true);
        Assert.Equal((4, 2, 8, 2), await ReadAsync(string.Empty));
        Assert.Equal((3, 2, 7, 1), await ReadAsync(centro));
        Assert.Equal((1, 0, 1, 1), await ReadAsync(north));
        Assert.Equal((2, 2, 6, 0), await ReadAsync(ana));
        Assert.Equal((1, 0, 1, 1), await ReadAsync(beto));
        Assert.Equal((1, 0, 1, 1), await ReadAsync("unassigned=true"));
        Assert.Equal((3, 2, 7, 1), await ReadAsync("source=Ads"));
        Assert.Equal((1, 0, 1, 1), await ReadAsync("source=Organic"));
        Assert.Equal((2, 2, 6, 0), await ReadAsync($"{centro}&{ana}&source=Ads"));
        Assert.Equal((1, 0, 1, 1), await ReadAsync($"{north}&source=Ads"));
        Assert.Equal((1, 0, 1, 1), await ReadAsync($"{centro}&unassigned=true&source=Organic"));

        await SetSandboxModeAsync(admin, t.TenantId, false);
        await AssertRealAsync();
    }

    [Fact]
    public async Task EachKindOfAlert_IsRaisedByItsOwnSideOfTheWallOnly()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        using var admin = await AdminAsync();
        var now = DateTime.UtcNow;
        var current = now.AddDays(-10); // inside the last 20 days, and matured (older than 7 days)
        var previous = now.AddDays(-30); // inside the 20 days before those
        var options = MetricsOptions.Default with { MinSampleSize = 2 };
        var period = new MetricsQuery { From = DateOnly.FromDateTime(now.AddDays(-20)), To = DateOnly.FromDateTime(now.AddDays(1)) };

        Task Seed(bool sandbox, bool north, DateTime start, OpportunityStage stage, params Msg[] messages)
            => SeedConversationAsync(
                _factory.Services, t.TenantId, north ? t.Second.Id : t.Centro.Id, north ? t.SecondChannel.Id : t.Fixture.Channel.Id, start,
                messages.Length == 0 ? [In(0), Out(30)] : messages, closed: stage != OpportunityStage.New, sandbox: sandbox,
                configureOpportunity: o => MetricsTenant.Decide(o, start, stage, stage == OpportunityStage.Lost ? LossReason.Price : null, ObjectionType.None));

        // Real: a healthy company. Both branches win everything, now and before, and nothing waits for an answer.
        foreach (var north in new[] { false, false, true, true })
        {
            await Seed(false, north, current, OpportunityStage.Won);
            await Seed(false, north, previous, OpportunityStage.Won);
        }

        // Sandbox: every alert at once. Centro loses everything (the company's rate falls from 100 % to 50 %, and Centro is 50 points
        // below it), and one customer has been waiting for three hours.
        foreach (var north in new[] { false, false, true, true })
        {
            await Seed(true, north, current, north ? OpportunityStage.Won : OpportunityStage.Lost);
            await Seed(true, north, previous, OpportunityStage.Won);
        }

        await Seed(true, false, now.AddHours(-3), OpportunityStage.New, In(0));

        async Task<AlertsResponse> AlertsAsync()
        {
            using var harness = new MetricsHarness(_factory.Services, t.TenantId, now, "UTC", options);
            return await harness.Service.GetAlertsAsync(period);
        }

        // Mode off: the sandbox data raises nothing.
        var real = await AlertsAsync();
        Assert.Empty(real.Alerts);
        Assert.Equal(0, real.Unanswered.Total);

        // Mode on: the three alerts, with the sandbox numbers exactly (the healthy real data is not averaged in).
        await SetSandboxModeAsync(admin, t.TenantId, true);
        var sandbox = await AlertsAsync();
        Assert.Equal(
            [AlertCodes.BranchBelowAverage, AlertCodes.ConversionDrop, AlertCodes.UnansweredOverSla],
            sandbox.Alerts.Select(a => a.Code).Order(StringComparer.Ordinal));

        var drop = sandbox.Alerts.Single(a => a.Code == AlertCodes.ConversionDrop);
        Assert.Equal((50.0, 100.0, 50.0, 4, 4), (Number(drop, "currentRate"), Number(drop, "previousRate"), Number(drop, "dropPoints"), (int)Number(drop, "currentSample"), (int)Number(drop, "previousSample")));

        var branch = sandbox.Alerts.Single(a => a.Code == AlertCodes.BranchBelowAverage);
        Assert.Equal(t.Centro.Id.ToString(), branch.Values["branchId"]!.ToString());
        Assert.Equal((0.0, 50.0, 50.0, 2), (Number(branch, "branchRate"), Number(branch, "tenantRate"), Number(branch, "gapPoints"), (int)Number(branch, "branchSample")));

        var unanswered = sandbox.Alerts.Single(a => a.Code == AlertCodes.UnansweredOverSla);
        Assert.Equal(1, (int)Number(unanswered, "count"));
        Assert.Equal(1, sandbox.Unanswered.Total);

        // And back: nothing again.
        await SetSandboxModeAsync(admin, t.TenantId, false);
        Assert.Empty((await AlertsAsync()).Alerts);
    }

    private static double Number(MetricAlert alert, string name) => Convert.ToDouble(alert.Values[name], System.Globalization.CultureInfo.InvariantCulture);
}
