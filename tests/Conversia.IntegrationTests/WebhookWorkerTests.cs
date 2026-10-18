using Conversia.Application.Interfaces;
using Conversia.Application.Ingestion;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Conversia.Infrastructure.Webhooks;
using Conversia.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The queue discipline of the worker: several workers never take the same event (<c>FOR UPDATE SKIP LOCKED</c>), a failing event backs
/// off and is finally marked Failed, and a failed attempt leaves nothing behind.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class WebhookWorkerTests : IAsyncLifetime
{
    private const long T0 = Dialog360Samples.T0;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WebhookWorkerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => ClearQueueAsync(_factory.Services);

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<ChannelFixture> NewChannelAsync() => StartChannelAsync(_factory, _client);

    private async Task<WebhookEvent> EventAsync(IServiceProvider services, Guid tenantId, int index = 0)
        => (await SnapshotAsync(services, tenantId)).Events[index];

    // ---- Concurrent workers --------------------------------------------------------------------------

    [Fact]
    public async Task SeveralWorkersDraining_ProcessEveryEventExactlyOnce_WithoutDuplicatesOrFailures()
    {
        using var f = await NewChannelAsync();
        const int customers = 3;
        const int messagesPerCustomer = 6;
        for (var m = 0; m < messagesPerCustomer; m++)
        {
            for (var c = 0; c < customers; c++)
            {
                await DeliverAsync(_client, f, Dialog360Samples.InboundText($"wamid.C{c}-M{m}", T0 + m, waId: $"57300555{c:0000}", name: $"Cliente {c}"));
            }
        }

        // Six workers at once on the same queue and the same tenant; the same customers keep appearing in different events.
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => DrainAsync(RunnerOf(_factory.Services)))));

        var s = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.Equal(customers * messagesPerCustomer, s.Events.Count);
        Assert.All(s.Events, e => Assert.Equal((WebhookEventStatus.Processed, 0, (string?)null), (e.Status, e.Attempts, e.LastError)));
        Assert.Equal(customers * messagesPerCustomer, s.Messages.Count);
        Assert.Equal(customers, s.Contacts.Count);
        Assert.Equal(customers, s.Conversations.Count);
        Assert.Equal(customers, s.Opportunities.Count);
    }

    [Fact]
    public async Task AnEventAnotherWorkerHasLocked_IsSkippedNotWaitedFor_AndProcessedOnceReleased()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.LOCKED", T0));
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.FREE", T0 + 1));
        var locked = (await SnapshotAsync(_factory.Services, f.TenantId)).Events[0];

        await using var connection = new NpgsqlConnection(_factory.ServerConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM \"WebhookEvents\" WHERE \"Id\" = @id FOR UPDATE", connection, transaction))
            {
                lockRow.Parameters.AddWithValue("id", locked.Id);
                await lockRow.ExecuteScalarAsync();
            }

            var runner = RunnerOf(_factory.Services);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); // a blocked claim would sit here
            Assert.True(await runner.ProcessNextAsync(timeout.Token)); // took the free one
            Assert.False(await runner.ProcessNextAsync(timeout.Token)); // the locked one is skipped, nothing else is due

            var during = (await SnapshotAsync(_factory.Services, f.TenantId)).Events;
            Assert.Equal(WebhookEventStatus.Pending, during.Single(e => e.Id == locked.Id).Status);
            Assert.Equal(WebhookEventStatus.Processed, during.Single(e => e.Id != locked.Id).Status);
            await transaction.RollbackAsync();
        }

        await DrainAsync(RunnerOf(_factory.Services));
        var after = await SnapshotAsync(_factory.Services, f.TenantId);
        Assert.All(after.Events, e => Assert.Equal(WebhookEventStatus.Processed, e.Status));
        Assert.Equal(2, after.Messages.Count);
    }

    [Fact]
    public async Task AnEventIsNotClaimedBeforeItsNextAttemptTime()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        await SetNextAttemptAsync(_factory.Services, f.TenantId, DateTime.UtcNow.AddHours(1));

        Assert.False(await RunnerOf(_factory.Services).ProcessNextAsync());
        Assert.Empty((await SnapshotAsync(_factory.Services, f.TenantId)).Messages);

        await SetNextAttemptAsync(_factory.Services, f.TenantId, DateTime.UtcNow.AddSeconds(-1));
        Assert.True(await RunnerOf(_factory.Services).ProcessNextAsync());
        Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Messages);
    }

    // ---- Failure, back-off, Failed ----------------------------------------------------------------------

    private WebApplicationFactory<Program> FailingHost(int failTimes, int maxAttempts = 3, string baseDelaySeconds = "3600")
        => WebhookHost(
            _factory,
            new()
            {
                ["Webhooks:Processing:MaxAttempts"] = maxAttempts.ToString(),
                ["Webhooks:Processing:RetryBaseDelaySeconds"] = baseDelaySeconds,
            },
            services =>
            {
                services.AddScoped<WebhookEventProcessor>();
                services.AddSingleton(new FailureCounter(failTimes));
                services.AddScoped<IWebhookEventProcessor, FailingProcessor>();
            });

    [Fact]
    public async Task AFailingEvent_BacksOffExponentially_AndIsMarkedFailedAtTheAttemptLimit_WithASanitizedError()
    {
        using var host = FailingHost(failTimes: int.MaxValue);
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        var runner = RunnerOf(host.Services);

        // Attempt 1: fails, stays pending, retried after the base delay (3600 s).
        Assert.True(await runner.ProcessNextAsync());
        var first = await EventAsync(host.Services, f.TenantId);
        Assert.Equal((WebhookEventStatus.Pending, 1), (first.Status, first.Attempts));
        AssertDueIn(first, TimeSpan.FromSeconds(3600));
        Assert.False(await runner.ProcessNextAsync()); // not due yet

        // Attempt 2: the delay doubles.
        await SetNextAttemptAsync(host.Services, f.TenantId, DateTime.UtcNow.AddSeconds(-1));
        Assert.True(await runner.ProcessNextAsync());
        var second = await EventAsync(host.Services, f.TenantId);
        Assert.Equal((WebhookEventStatus.Pending, 2), (second.Status, second.Attempts));
        AssertDueIn(second, TimeSpan.FromSeconds(7200));

        // Attempt 3 is the last.
        await SetNextAttemptAsync(host.Services, f.TenantId, DateTime.UtcNow.AddSeconds(-1));
        Assert.True(await runner.ProcessNextAsync());
        var last = await EventAsync(host.Services, f.TenantId);
        Assert.Equal((WebhookEventStatus.Failed, 3), (last.Status, last.Attempts));

        // Nothing retries a Failed event, even when it looks due.
        await SetNextAttemptAsync(host.Services, f.TenantId, DateTime.UtcNow.AddSeconds(-1));
        Assert.False(await runner.ProcessNextAsync());

        // The error is kept, but with the phone number masked and the secret redacted.
        Assert.StartsWith("InvalidOperationException:", last.LastError);
        Assert.Contains("[num]", last.LastError);
        Assert.DoesNotContain("573005550101", last.LastError);
        Assert.DoesNotContain("hunter2", last.LastError);
        Assert.True(last.LastError!.Length <= WebhookEvent.LastErrorMaxLength);

        // A failed event is also visible in the error registry.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registered = await db.ErrorLogs.AsNoTracking().Where(e => e.Context!.Contains(last.Id.ToString())).ToListAsync();
        var logged = Assert.Single(registered);
        Assert.Equal((ErrorSource.Api, f.TenantId), (logged.Source, logged.TenantId));
        Assert.DoesNotContain("hunter2", logged.ExceptionMessage);
        Assert.DoesNotContain("573005550101", logged.ExceptionMessage);

        // And nothing of the failed attempts is left behind.
        var s = await SnapshotAsync(host.Services, f.TenantId);
        Assert.Empty(s.Messages);
        Assert.Empty(s.Contacts);
        Assert.Empty(s.Conversations);
    }

    [Fact]
    public async Task AnEventThatFailsOnceAndThenSucceeds_IsProcessedExactlyOnce()
    {
        using var host = FailingHost(failTimes: 1);
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        var runner = RunnerOf(host.Services);

        Assert.True(await runner.ProcessNextAsync());
        Assert.Equal(1, (await EventAsync(host.Services, f.TenantId)).Attempts);
        Assert.Empty((await SnapshotAsync(host.Services, f.TenantId)).Messages); // the failed attempt was rolled back whole

        await SetNextAttemptAsync(host.Services, f.TenantId, DateTime.UtcNow.AddSeconds(-1));
        Assert.True(await runner.ProcessNextAsync());

        var s = await SnapshotAsync(host.Services, f.TenantId);
        var processed = Assert.Single(s.Events);
        Assert.Equal((WebhookEventStatus.Processed, 1, (string?)null), (processed.Status, processed.Attempts, processed.LastError));
        Assert.Single(s.Messages);
        Assert.Single(s.Conversations);
        Assert.Single(s.Opportunities);
    }

    [Fact]
    public async Task AFailingEventDoesNotBlockTheOnesBehindIt()
    {
        using var host = FailingHost(failTimes: 1);
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.FIRST", T0));
        await DeliverAsync(_client, f, Dialog360Samples.InboundText("wamid.SECOND", T0 + 1));

        await DrainAsync(RunnerOf(host.Services));

        var s = await SnapshotAsync(host.Services, f.TenantId);
        Assert.Equal("wamid.SECOND", Assert.Single(s.Messages).ProviderMessageId);
        Assert.Equal([WebhookEventStatus.Pending, WebhookEventStatus.Processed], s.Events.Select(e => e.Status));
    }

    // ---- The hosted service ------------------------------------------------------------------------------

    [Fact]
    public async Task TheHostedWorker_PicksUpAStoredEventOnItsOwn()
    {
        using var host = WebhookHost(_factory, new() { ["Webhooks:Processing:Enabled"] = "true", ["Webhooks:Processing:PollIntervalMilliseconds"] = "50" });
        using var f = await NewChannelAsync();
        using var client = host.CreateClient(); // starts the host, and with it the worker

        await DeliverAsync(client, f, Dialog360Samples.InboundText());

        var deadline = DateTime.UtcNow.AddSeconds(30);
        Snapshot s;
        do
        {
            await Task.Delay(100);
            s = await SnapshotAsync(host.Services, f.TenantId);
        }
        while (s.Events.Any(e => e.Status == WebhookEventStatus.Pending) && DateTime.UtcNow < deadline);

        Assert.Equal(WebhookEventStatus.Processed, Assert.Single(s.Events).Status);
        Assert.Single(s.Messages);
        Assert.Single(s.Opportunities);
    }

    [Fact]
    public async Task WithTheWorkerDisabled_EventsStayPending()
    {
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        await Task.Delay(500);

        Assert.Equal(WebhookEventStatus.Pending, Assert.Single((await SnapshotAsync(_factory.Services, f.TenantId)).Events).Status);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------

    private static void AssertDueIn(WebhookEvent e, TimeSpan expected)
    {
        var due = e.NextAttemptAt - DateTime.UtcNow;
        Assert.InRange(due, expected - TimeSpan.FromSeconds(120), expected + TimeSpan.FromSeconds(5));
    }

    private static async Task SetNextAttemptAsync(IServiceProvider services, Guid tenantId, DateTime at)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlAsync($"UPDATE \"WebhookEvents\" SET \"NextAttemptAt\" = {at} WHERE \"TenantId\" = {tenantId} AND \"Status\" = 'Pending'");
    }

    private sealed class FailureCounter(int failTimes)
    {
        private int _failures;

        public bool ShouldFail() => Interlocked.Increment(ref _failures) <= failTimes;
    }

    /// <summary>Does the real work, then fails: so a test can tell that a failed attempt is rolled back whole.</summary>
    private sealed class FailingProcessor(WebhookEventProcessor inner, FailureCounter counter) : IWebhookEventProcessor
    {
        public async Task<WebhookProcessingResult> ProcessAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default)
        {
            var result = await inner.ProcessAsync(webhookEvent, cancellationToken);
            if (counter.ShouldFail())
            {
                throw new InvalidOperationException("boom for customer 573005550101 with password=hunter2");
            }

            return result;
        }
    }
}
