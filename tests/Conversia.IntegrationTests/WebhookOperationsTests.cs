using System.Net;
using Conversia.API.Configuration;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Conversia.Infrastructure.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>Operating the webhook queue: <c>GET /health</c> and the retention of stored events.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class WebhookOperationsTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WebhookOperationsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>Starts from a calm queue: nothing pending, and no failure in the last hour.</summary>
    public async Task InitializeAsync()
    {
        await ClearQueueAsync(_factory.Services);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("UPDATE \"WebhookEvents\" SET \"UpdatedAt\" = now() - interval '2 days' WHERE \"Status\" = 'Failed'");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> SeedAsync(ChannelFixture f, WebhookEventStatus status, TimeSpan age, TimeSpan? lastTouchedAgo = null)
    {
        var webhookEvent = WebhookEvent.Receive(f.TenantId, f.Channel.Id, Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray(), "{}", DateTime.UtcNow);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Add(webhookEvent);
        await db.SaveChangesAsync();
        var received = DateTime.UtcNow - age;
        var touched = DateTime.UtcNow - (lastTouchedAgo ?? age);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE "WebhookEvents"
            SET "Status" = {status.ToString()}, "ReceivedAt" = {received}, "ProcessedAt" = {(status == WebhookEventStatus.Processed ? touched : (DateTime?)null)}, "UpdatedAt" = {touched}
            WHERE "Id" = {webhookEvent.Id}
            """);
        return webhookEvent.Id;
    }

    private async Task<bool> ExistsAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().WebhookEvents.IgnoreQueryFilters().AnyAsync(e => e.Id == id);
    }

    // ---- /health --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Health_IsAnonymousAndMinimal_AndHealthyWhileTheQueueFlows()
    {
        using var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_IsDegraded_WhenTheOldestPendingEventHasWaitedTooLong()
    {
        using var f = await StartChannelAsync(_factory, _client);
        await SeedAsync(f, WebhookEventStatus.Pending, TimeSpan.FromMinutes(30));

        using var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // degraded is still a 200: the service answers, the queue is late
        Assert.Equal("Degraded", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_IsDegraded_WhenTooManyEventsFailedInTheLastHour_AndWhenTheyAreOldItRecovers()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await SeedAsync(f, WebhookEventStatus.Failed, TimeSpan.FromMinutes(5)));
        }

        using var degraded = await _client.GetAsync("/health");
        Assert.Equal("Degraded", await degraded.Content.ReadAsStringAsync());

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database
                .ExecuteSqlAsync($"UPDATE \"WebhookEvents\" SET \"UpdatedAt\" = now() - interval '3 hours' WHERE \"TenantId\" = {f.TenantId}");
        }

        using var healthy = await _client.GetAsync("/health");
        Assert.Equal("Healthy", await healthy.Content.ReadAsStringAsync());
    }

    // ---- Retention ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePurger_DeletesProcessedEventsAfterTheRetention_FailedOnesLater_AndNeverPendingOnes()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var oldProcessed = await SeedAsync(f, WebhookEventStatus.Processed, TimeSpan.FromDays(20));
        var recentProcessed = await SeedAsync(f, WebhookEventStatus.Processed, TimeSpan.FromDays(1));
        var oldFailed = await SeedAsync(f, WebhookEventStatus.Failed, TimeSpan.FromDays(70));
        var midFailed = await SeedAsync(f, WebhookEventStatus.Failed, TimeSpan.FromDays(30));
        var ancientPending = await SeedAsync(f, WebhookEventStatus.Pending, TimeSpan.FromDays(100));

        using var scope = _factory.Services.CreateScope();
        var (processed, failed) = await scope.ServiceProvider.GetRequiredService<WebhookEventPurger>()
            .PurgeAsync(DateTime.UtcNow.AddDays(-14), DateTime.UtcNow.AddDays(-60));

        Assert.True(processed >= 1 && failed >= 1); // at least ours: leftovers of other tests may be old too
        Assert.False(await ExistsAsync(oldProcessed));
        Assert.False(await ExistsAsync(oldFailed));
        Assert.True(await ExistsAsync(recentProcessed));
        Assert.True(await ExistsAsync(midFailed));
        Assert.True(await ExistsAsync(ancientPending));
    }

    [Fact]
    public async Task ThePurger_WorksInBatches()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var count = WebhookEventPurger.BatchSize + 25;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "WebhookEvents" ("Id", "TenantId", "ChannelId", "BodyHash", "RawPayload", "ReceivedAt", "Status", "Attempts", "NextAttemptAt", "ProcessedAt", "CreatedAt")
                SELECT gen_random_uuid(), {f.TenantId}, {f.Channel.Id}, sha256(i::text::bytea), to_jsonb(1), now() - interval '40 days', 'Processed', 1, now(), now() - interval '40 days', now()
                FROM generate_series(1, {count}) AS i
                """);
        }

        using var purge = _factory.Services.CreateScope();
        var (processed, _) = await purge.ServiceProvider.GetRequiredService<WebhookEventPurger>().PurgeAsync(DateTime.UtcNow.AddDays(-14), DateTime.UtcNow.AddDays(-60));

        Assert.True(processed >= count);
    }

    [Fact]
    public async Task TheRetentionService_PurgesAtStartup_WhenEnabled()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var old = await SeedAsync(f, WebhookEventStatus.Processed, TimeSpan.FromDays(30));
        var fresh = await SeedAsync(f, WebhookEventStatus.Processed, TimeSpan.FromHours(1));
        using var host = WebhookHost(_factory, new() { ["Webhooks:Retention:Enabled"] = "true" });

        using var client = host.CreateClient(); // starts the host, and with it the retention service
        await EventuallyAsync(async () => !await ExistsAsync(old), "the old event is purged");

        Assert.True(await ExistsAsync(fresh));
    }

    [Fact]
    public async Task TheRetentionService_IsNotRunningWhenDisabled_WhichIsHowTheTestHostsRun()
    {
        var disabled = _factory.Services.GetServices<IHostedService>().Select(s => s.GetType().Name);
        using var enabledHost = WebhookHost(_factory, new() { ["Webhooks:Retention:Enabled"] = "true" });
        var enabled = enabledHost.Services.GetServices<IHostedService>().Select(s => s.GetType().Name);

        Assert.DoesNotContain("WebhookEventRetentionService", disabled);
        Assert.Contains("WebhookEventRetentionService", enabled);
    }

    [Fact]
    public void TheRetentionDefaultsAre14DaysFor_ProcessedAnd60ForFailedEvents_AndBadValuesStopTheApiAtStartup()
    {
        static IConfiguration Config(params (string, string)[] values) => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

        var defaults = WebhookOptionsValidator.GetValidatedRetentionOptions(Config());
        Assert.Equal((true, 14, 60), (defaults.Enabled, defaults.RetentionDays, defaults.FailedRetentionDays));

        var custom = WebhookOptionsValidator.GetValidatedRetentionOptions(Config(("Webhooks:Retention:RetentionDays", "3"), ("Webhooks:Retention:Enabled", "false")));
        Assert.Equal((false, 3), (custom.Enabled, custom.RetentionDays));

        foreach (var (key, value) in new[] { ("Webhooks:Retention:RetentionDays", "0"), ("Webhooks:Retention:FailedRetentionDays", "many"), ("Webhooks:Retention:Enabled", "maybe") })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => WebhookOptionsValidator.GetValidatedRetentionOptions(Config((key, value))));
            Assert.Contains(key, ex.Message);
        }
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string description, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Not true within {timeoutSeconds}s: {description}");
            await Task.Delay(50);
        }
    }
}
