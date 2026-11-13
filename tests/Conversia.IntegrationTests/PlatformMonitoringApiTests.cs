using System.Net;
using System.Net.Http.Json;
using Conversia.Application.Classification.Llm;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The administrator's monitoring (docs/PLATFORM_ADMIN.md, "Monitoring") on a real PostgreSQL: counts per window and per day, real and sandbox
/// apart, and never a message, a number, a payload or a contact.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class PlatformMonitoringApiTests
{
    private const string Secret = "texto-privado-del-cliente";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PlatformMonitoringApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string TenantUrl(Guid id) => $"{AdminTenantsUrl}/{id}/monitoring";

    private async Task AddDeliveryAsync(ChannelFixture f, DateTime receivedAt, bool sandbox, int stored, int duplicates, bool failed = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var delivery = WebhookEvent.Receive(f.TenantId, f.Channel.Id, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            $$"""{"text":"{{Secret}}"}""", receivedAt, sandbox);
        delivery.RecordParseCounts(skippedMessages: 1, statuses: 0, storedMessages: stored, duplicateMessages: duplicates);
        if (failed)
        {
            delivery.Fail("boom");
        }
        else
        {
            delivery.MarkProcessed(receivedAt);
        }

        db.WebhookEvents.Add(delivery);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ACompanysView_CountsItsDaysDeliveriesAndSends_RealAndSandboxApart()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client);
        var now = DateTime.UtcNow.AddMinutes(-10);
        var waId = UniqueWaId();
        await SeedConversationAsync(_factory.Services, f, now, [In(0, Secret), Out(30, "Hola"), In(60, "Gracias")], waId: waId, closed: true);
        await SeedConversationAsync(_factory.Services, f, now, [In(0, "prueba")], sandbox: true);
        await AddDeliveryAsync(f, now, sandbox: false, stored: 3, duplicates: 1);
        await AddDeliveryAsync(f, now, sandbox: false, stored: 0, duplicates: 0, failed: true);
        await AddDeliveryAsync(f, now, sandbox: true, stored: 1, duplicates: 0);
        await AddDeliveryAsync(f, now.AddDays(-3), sandbox: false, stored: 2, duplicates: 0);
        var outcomes = _factory.Services.GetRequiredService<IMessageSendOutcomeRecorder>();
        await outcomes.RecordAsync(f.TenantId, false, MessageSendOutcome.Sent);
        await outcomes.RecordAsync(f.TenantId, false, "provider_limit_reached");
        await outcomes.RecordAsync(f.TenantId, true, MessageSendOutcome.Sent);

        var response = await admin.Client.GetAsync(TenantUrl(f.TenantId));
        var body = await response.Content.ReadAsStringAsync();
        var view = (await response.Content.ReadFromJsonAsync<TenantMonitoringResponse>())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var today = AiQuotaSettings.Default.DayOf(now).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var real = Assert.Single(view.Days, d => d.Day == today && !d.IsSandbox);
        var sandbox = Assert.Single(view.Days, d => d.Day == today && d.IsSandbox);
        Assert.Equal((2, 1, 1, 1), (real.MessagesIn, real.MessagesOut, real.ConversationsOpened, real.ConversationsClosed));
        Assert.Equal((1, 0, 1, 0), (sandbox.MessagesIn, sandbox.MessagesOut, sandbox.ConversationsOpened, sandbox.ConversationsClosed));

        Assert.Equal((2, 3, 1, 2, 1), (view.Deliveries.RealLast24h.Events, view.Deliveries.RealLast24h.StoredMessages, view.Deliveries.RealLast24h.DuplicateMessages,
            view.Deliveries.RealLast24h.SkippedMessages, view.Deliveries.RealLast24h.FailedEvents));
        Assert.Equal((3, 5), (view.Deliveries.RealLast7d.Events, view.Deliveries.RealLast7d.StoredMessages));
        Assert.Equal((1, 1), (view.Deliveries.SandboxLast24h.Events, view.Deliveries.SandboxLast24h.StoredMessages));
        Assert.NotNull(view.LastRealDeliveryAt);

        Assert.Equal((2, 1), (view.Sends.RealLast7d, view.Sends.RealFailedLast7d));
        var sent = Assert.Single(view.Sends.ByOutcomeLast7d, o => o.Outcome == "sent");
        Assert.Equal((1, 1), (sent.Real, sent.Sandbox));
        Assert.Equal(("Disabled", false, 500), (view.Ai.State, view.Ai.Enabled, view.Ai.Quota));
        Assert.Equal((1, 50), (view.ActiveLines, view.MaxActiveLines));

        Assert.DoesNotContain(Secret, body);
        Assert.DoesNotContain(waId, body);
        Assert.DoesNotContain("Cliente", body, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", body);
    }

    [Fact]
    public async Task TheOverview_FlagsACompanyWhoseLinesAreSilent_AndOneThatUsedUpItsAi_WithoutPrivateContent()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var silent = await StartChannelAsync(_factory, _client);
        using var limited = await StartTenantAsync(_factory, _client);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tenant = await db.Tenants.SingleAsync(t => t.Id == limited.TenantId);
            tenant.SetAiEnabled(true, "v1", DateTime.UtcNow);
            tenant.SetAiMonthlyConversationQuota(1);
            await db.SaveChangesAsync();
        }

        await new Infrastructure.Classification.Usage.AiUsageMeter(
                _factory.Services.GetRequiredService<IServiceScopeFactory>(), AiQuotaSettings.Default, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Classification.Usage.AiUsageMeter>.Instance)
            .RecordSuccessAsync(new AiUsageRecord(limited.TenantId, Guid.NewGuid(), false, new Application.Classification.Llm.LlmUsage(100, 10, 0, 0), 0.001m));
        await SeedConversationAsync(_factory.Services, silent, DateTime.UtcNow.AddMinutes(-1), [In(0, Secret)]);

        var response = await admin.Client.GetAsync("/api/admin/monitoring");
        var body = await response.Content.ReadAsStringAsync();
        var overview = (await response.Content.ReadFromJsonAsync<PlatformOverviewResponse>())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(overview.Health.Checks);
        Assert.Contains(overview.Flags, f => f.Code == "silent_companies");
        Assert.Contains(overview.Flags, f => f.Code == "ai_limited_companies");
        Assert.True(overview.SilentCompaniesTotal >= 1);
        Assert.True(overview.AiLimitedCompaniesTotal >= 1);
        Assert.Equal(24, overview.Thresholds.SilentLineHours);
        Assert.Equal("Rules", overview.Ai.Provider);
        Assert.Null(overview.Ai.BreakerState);
        Assert.DoesNotContain(Secret, body);
    }

    [Fact]
    public async Task AnUnknownCompanyIsNotFound_AndACompanyTokenIsRefused()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var tenant = await StartTenantAsync(_factory, _client);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync(TenantUrl(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tenant.Owner.Client.GetAsync("/api/admin/monitoring")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tenant.Owner.Client.GetAsync(TenantUrl(tenant.TenantId))).StatusCode);
    }

    private sealed class CommandRecorder : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public System.Collections.Concurrent.ConcurrentQueue<(string Text, int Timeout)> Commands { get; } = new();

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Enqueue((command.CommandText, command.CommandTimeout));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task TheMonitoringQueries_RunWithTheirOwnShortTimeout_OneStatementPerList()
    {
        var recorder = new CommandRecorder();
        await using var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.ConfigureDbContext<ApplicationDbContext>(o => o.AddInterceptors(recorder))));
        using var admin = await StartAdminAsync(_factory, _client);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin.Token);
        using var tenant = await StartTenantAsync(_factory, _client);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/monitoring")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(TenantUrl(tenant.TenantId))).StatusCode);

        var monitoring = recorder.Commands.Where(c => c.Text.Contains("\"MessageSendOutcomes\"", StringComparison.Ordinal)
            || (c.Text.Contains("\"SandboxMode\"", StringComparison.Ordinal) && c.Text.Contains("\"WebhookEvents\"", StringComparison.Ordinal))
            || c.Text.Contains("\"ErrorLogs\"", StringComparison.Ordinal) || c.Text.Contains("\"AiUsageMonths\"", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(monitoring);
        Assert.All(monitoring, c => Assert.Equal(Infrastructure.Monitoring.PlatformMonitoringQueries.CommandTimeoutSeconds, c.Timeout));
        Assert.Equal(10, Infrastructure.Monitoring.PlatformMonitoringQueries.CommandTimeoutSeconds);
        Assert.Equal(2, recorder.Commands.Count(c => c.Text.Contains("\"ErrorLogs\"", StringComparison.Ordinal))); // overview and company: one each
        Assert.Single(recorder.Commands, c => c.Text.Contains("\"SandboxMode\"", StringComparison.Ordinal) && c.Text.Contains("\"WebhookEvents\"", StringComparison.Ordinal)); // silent companies
    }

    [Fact]
    public async Task ACompanysView_ShowsItsAiSchedule_TheNextWindow_ThePendingConversations_AndTheLastRun_AndTheOverviewTheBatches()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var f = await StartChannelAsync(_factory, _client);
        await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddHours(-2), [In(0, Secret)], requestClassification: true);
        var window = new DateTime(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE \"Conversations\" SET \"ClassificationDirty\" = false WHERE \"TenantId\" = {f.TenantId}");
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "AiAnalysisRuns" ("TenantId", "WindowStart", "WindowEnd", "Mode", "StartedAt", "LastActivityAt", "Claimed", "Analysed", "Unchanged",
                    "Skipped", "Discarded", "Failed", "Deferred", "Batched", "Calls")
                VALUES ({f.TenantId}, {window.AddDays(-1)}, {window.AddDays(-1).AddHours(5)}, 'Nightly', {window.AddDays(-1)}, {window.AddDays(-1).AddMinutes(9)}, 3, 3, 0, 0, 0, 0, 0, 3, 3),
                       ({f.TenantId}, {window}, {window.AddHours(5)}, 'Nightly', {window.AddMinutes(1)}, {window.AddMinutes(20)}, 9, 5, 1, 1, 1, 1, 0, 6, 7)
                """);
            var batch = LlmBatch.Prepare(2, DateTime.UtcNow);
            batch.MarkSubmitted("msgbatch_monitoring", DateTime.UtcNow.AddHours(24), DateTime.UtcNow);
            db.LlmBatches.Add(batch);
            await db.SaveChangesAsync();
        }

        var response = await admin.Client.GetAsync(TenantUrl(f.TenantId));
        var body = await response.Content.ReadAsStringAsync();
        var view = (await response.Content.ReadFromJsonAsync<TenantMonitoringResponse>())!;

        var analysis = view.AiAnalysis;
        Assert.Equal(("Nightly", (int?)1, 1), (analysis.Mode, analysis.NightlyHour, analysis.Pending));
        Assert.NotNull(analysis.NextWindowAt);
        var last = analysis.LastRun!;
        Assert.Equal((window, window.AddHours(5), window.AddMinutes(20)), (last.WindowStart, last.WindowEnd, last.LastActivityAt));
        Assert.Equal((9, 5, 1, 1, 1, 1, 0, 6), (last.Claimed, last.Analysed, last.Unchanged, last.Skipped, last.Discarded, last.Failed, last.Deferred, last.Batched));
        Assert.DoesNotContain(Secret, body);

        var overview = (await admin.Client.GetFromJsonAsync<PlatformOverviewResponse>("/api/admin/monitoring"))!;
        Assert.True(overview.Ai.Batches.Submitted >= 1);
        Assert.NotNull(overview.Ai.Batches.OldestSubmittedAt);
    }
}
