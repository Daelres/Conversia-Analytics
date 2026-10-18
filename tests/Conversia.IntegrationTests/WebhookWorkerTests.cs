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
        Assert.All(s.Events, e => Assert.Equal((WebhookEventStatus.Processed, 1, (string?)null), (e.Status, e.Attempts, e.LastError)));
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
        Assert.Equal((WebhookEventStatus.Processed, 2, (string?)null), (processed.Status, processed.Attempts, processed.LastError)); // the failed attempt and the good one
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

    // ---- A busy tenant never starves the queue -------------------------------------------------------------

    [Fact]
    public async Task AnEventWhoseTenantIsBusyElsewhere_IsDeferredWithoutConsumingAnAttempt_WhileOtherTenantsGoOn()
    {
        using var host = WebhookHost(_factory, new() { ["Webhooks:Processing:TenantLockWaitMilliseconds"] = "50", ["Webhooks:Processing:TenantBusyDelaySeconds"] = "10" });
        using var busy = await NewChannelAsync();
        using var free = await NewChannelAsync();
        await DeliverAsync(_client, busy, Dialog360Samples.InboundText("wamid.BUSY", T0));
        await DeliverAsync(_client, free, Dialog360Samples.InboundText("wamid.FREE", T0 + 1));

        await using var other = new NpgsqlConnection(_factory.ServerConnectionString);
        await other.OpenAsync();
        await ExecuteAsync(other, "SELECT pg_advisory_lock(hashtextextended(@t, 0))", ("t", busy.TenantId.ToString())); // another instance is processing that tenant

        var runner = RunnerOf(host.Services);
        Assert.True(await runner.ProcessNextAsync()); // claims the busy tenant's (older) event and defers it
        Assert.True(await runner.ProcessNextAsync()); // the next one in line is not held up behind it
        Assert.False(await runner.ProcessNextAsync());

        var deferred = await EventAsync(host.Services, busy.TenantId);
        Assert.Equal((WebhookEventStatus.Pending, 0, (string?)null), (deferred.Status, deferred.Attempts, deferred.LastError));
        var due = deferred.NextAttemptAt - DateTime.UtcNow;
        Assert.InRange(due, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(16)); // 10 s, jittered between half and one and a half
        Assert.Equal(WebhookEventStatus.Processed, (await EventAsync(host.Services, free.TenantId)).Status);

        await ExecuteAsync(other, "SELECT pg_advisory_unlock(hashtextextended(@t, 0))", ("t", busy.TenantId.ToString()));
        await SetNextAttemptAsync(host.Services, busy.TenantId, DateTime.UtcNow.AddSeconds(-1));
        Assert.True(await runner.ProcessNextAsync());
        var done = await EventAsync(host.Services, busy.TenantId);
        Assert.Equal((WebhookEventStatus.Processed, 1), (done.Status, done.Attempts));
    }

    // ---- Failures anywhere after the claim are recorded ------------------------------------------------------------

    [Fact]
    public async Task AFailureBeforeProcessingEvenStarts_RecordsTheAttempt_BacksOff_AndEndsFailed()
    {
        using var host = WebhookHost(
            _factory,
            new() { ["Webhooks:Processing:MaxAttempts"] = "3", ["Webhooks:Processing:RetryBaseDelaySeconds"] = "3600" },
            services => services.AddScoped<ITenantContext, ThrowingTenantContext>());
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        var runner = RunnerOf(host.Services);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.True(await runner.ProcessNextAsync()); // the same event is not claimed again and again: its attempt is recorded
            var current = await EventAsync(host.Services, f.TenantId);
            Assert.Equal(attempt, current.Attempts);
            Assert.StartsWith("InvalidOperationException:", current.LastError);
            if (attempt < 3)
            {
                Assert.Equal(WebhookEventStatus.Pending, current.Status);
                AssertDueIn(current, TimeSpan.FromSeconds(3600 * Math.Pow(2, attempt - 1)));
                Assert.False(await runner.ProcessNextAsync());
                await SetNextAttemptAsync(host.Services, f.TenantId, DateTime.UtcNow.AddSeconds(-1));
            }
            else
            {
                Assert.Equal(WebhookEventStatus.Failed, current.Status);
            }
        }
    }

    [Fact]
    public async Task ATransactionKilledByTheStatementTimeout_StillGetsItsFailureRecorded()
    {
        using var host = WebhookHost(
            _factory,
            new() { ["Webhooks:Processing:ProcessingTimeoutSeconds"] = "1", ["Webhooks:Processing:RetryBaseDelaySeconds"] = "3600" },
            services => services.AddScoped<IWebhookEventProcessor, SleepingProcessor>());
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());

        Assert.True(await RunnerOf(host.Services).ProcessNextAsync());

        var current = await EventAsync(host.Services, f.TenantId);
        Assert.Equal((WebhookEventStatus.Pending, 1), (current.Status, current.Attempts));
        Assert.NotNull(current.LastError);
        AssertDueIn(current, TimeSpan.FromSeconds(3600));
    }

    [Fact]
    public async Task TheProcessingTransaction_RunsWithTheConfiguredStatementAndIdleTimeouts()
    {
        using var host = WebhookHost(
            _factory,
            new() { ["Webhooks:Processing:ProcessingTimeoutSeconds"] = "45" },
            services =>
            {
                services.AddSingleton<SettingsProbe>();
                services.AddScoped<IWebhookEventProcessor, SettingsProcessor>();
            });
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());

        Assert.True(await RunnerOf(host.Services).ProcessNextAsync());

        var probe = host.Services.GetRequiredService<SettingsProbe>();
        Assert.Equal(("45s", "45s"), (probe.StatementTimeout, probe.IdleTimeout));
    }

    // ---- Crashes, leases, size ------------------------------------------------------------------------------------

    [Fact]
    public async Task AClaimedEvent_IsLeased_SoNoOtherWorkerTakesItWhileOneIsWorkingOnIt()
    {
        var gate = new Gate();
        using var host = WebhookHost(_factory, new(), services =>
        {
            services.AddSingleton(gate);
            services.AddScoped<IWebhookEventProcessor, GatedProcessor>();
        });
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        var runner = RunnerOf(host.Services);

        var working = runner.ProcessNextAsync(); // blocks inside the processor
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(await runner.ProcessNextAsync()); // nothing else is due: the event is leased
        var leased = await EventAsync(host.Services, f.TenantId);
        Assert.Equal((WebhookEventStatus.Pending, 1), (leased.Status, leased.Attempts));
        AssertDueIn(leased, TimeSpan.FromSeconds(130)); // twice the 60 s processing timeout plus a margin

        gate.Release.SetResult();
        Assert.True(await working);
        Assert.Equal(WebhookEventStatus.Processed, (await EventAsync(host.Services, f.TenantId)).Status);
    }

    [Fact]
    public async Task AnEventThatUsedAllItsAttemptsWithoutFinishing_AfterWorkerCrashes_IsFailedOnClaimNotProcessedAgain()
    {
        using var host = WebhookHost(_factory, new() { ["Webhooks:Processing:MaxAttempts"] = "3" });
        using var f = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.InboundText());
        using (var scope = host.Services.CreateScope()) // three claims that never reported back: what a poison payload leaves behind
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database
                .ExecuteSqlAsync($"UPDATE \"WebhookEvents\" SET \"Attempts\" = 3 WHERE \"TenantId\" = {f.TenantId}");
        }

        Assert.True(await RunnerOf(host.Services).ProcessNextAsync());

        var s = await SnapshotAsync(host.Services, f.TenantId);
        var failed = Assert.Single(s.Events);
        Assert.Equal((WebhookEventStatus.Failed, 3), (failed.Status, failed.Attempts));
        Assert.Contains("without finishing", failed.LastError);
        Assert.Empty(s.Messages);
    }

    [Fact]
    public async Task ADeliveryOverTheMessageCap_IsFailedAtOnceWithoutRetries_AndNothingIsStored()
    {
        using var host = WebhookHost(_factory, new() { ["Webhooks:Processing:MaxMessagesPerDelivery"] = "2" });
        using var f = await NewChannelAsync();
        string Message(int i) => $$"""{ "from": "{{Dialog360Samples.CustomerWaId}}", "id": "wamid.M{{i}}", "timestamp": "{{T0 + i}}", "type": "text", "text": { "body": "x" } }""";
        await DeliverAsync(_client, f, $$"""{ "messages": [ {{Message(1)}}, {{Message(2)}}, {{Message(3)}} ] }""");

        Assert.True(await RunnerOf(host.Services).ProcessNextAsync());

        var s = await SnapshotAsync(host.Services, f.TenantId);
        var failed = Assert.Single(s.Events);
        Assert.Equal((WebhookEventStatus.Failed, 1), (failed.Status, failed.Attempts));
        Assert.Contains("3 messages, more than the 2 allowed", failed.LastError);
        Assert.Empty(s.Messages);
        Assert.Empty(s.Contacts);
        await SetNextAttemptAsync(host.Services, f.TenantId, DateTime.UtcNow.AddSeconds(-1));
        Assert.False(await RunnerOf(host.Services).ProcessNextAsync()); // never retried
    }

    // ---- Requeue ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedEvent_CanBeRequeuedForItsTenantOnly_AndThenProcessesNormally()
    {
        using var capped = WebhookHost(_factory, new() { ["Webhooks:Processing:MaxMessagesPerDelivery"] = "1" });
        using var f = await NewChannelAsync();
        using var other = await NewChannelAsync();
        await DeliverAsync(_client, f, Dialog360Samples.TwoInboundMessages());
        Assert.True(await RunnerOf(capped.Services).ProcessNextAsync());
        Assert.Equal(WebhookEventStatus.Failed, (await EventAsync(_factory.Services, f.TenantId)).Status);

        using var scope = _factory.Services.CreateScope();
        var maintenance = scope.ServiceProvider.GetRequiredService<IWebhookEventMaintenance>();
        Assert.Equal(0, await maintenance.RequeueFailedAsync(other.TenantId)); // another tenant's operator reaches nothing of ours
        using var scope2 = _factory.Services.CreateScope();
        Assert.Equal(1, await scope2.ServiceProvider.GetRequiredService<IWebhookEventMaintenance>().RequeueFailedAsync(f.TenantId));

        var requeued = await EventAsync(_factory.Services, f.TenantId);
        Assert.Equal((WebhookEventStatus.Pending, 0, (string?)null), (requeued.Status, requeued.Attempts, requeued.LastError));
        var s = await ProcessAllAsync(f);
        Assert.Equal(WebhookEventStatus.Processed, Assert.Single(s.Events).Status);
        Assert.Equal(2, s.Messages.Count);
    }

    [Fact]
    public void TheWorkerLoopWaitsLongerAfterEachFailedCycle_UpToAMinute()
    {
        Assert.Equal([2, 4, 8, 16, 32, 60, 60], Enumerable.Range(1, 7).Select(n => (int)WebhookWorkerBackoff.FailureDelay(n).TotalSeconds));
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

    private async Task<Snapshot> ProcessAllAsync(ChannelFixture f)
    {
        await DrainAsync(RunnerOf(_factory.Services));
        return await SnapshotAsync(_factory.Services, f.TenantId);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, (string Name, string Value) parameter)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteScalarAsync();
    }

    private sealed class ThrowingTenantContext : ITenantContext
    {
        public void SetTenant(Guid tenantId) => throw new InvalidOperationException("tenant context unavailable for 573005550101");
    }

    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatedProcessor(Gate gate) : IWebhookEventProcessor
    {
        public async Task<WebhookProcessingResult> ProcessAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default)
        {
            gate.Entered.SetResult();
            await gate.Release.Task;
            return new WebhookProcessingResult(0, 0, 0, 0, 0);
        }
    }

    /// <summary>Runs a statement longer than the statement timeout: PostgreSQL aborts it and the transaction with it.</summary>
    private sealed class SleepingProcessor(ApplicationDbContext db) : IWebhookEventProcessor
    {
        public async Task<WebhookProcessingResult> ProcessAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default)
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(5)", cancellationToken);
            return new WebhookProcessingResult(0, 0, 0, 0, 0);
        }
    }

    private sealed class SettingsProbe
    {
        public string? StatementTimeout { get; set; }

        public string? IdleTimeout { get; set; }
    }

    private sealed class SettingsProcessor(ApplicationDbContext db, SettingsProbe probe) : IWebhookEventProcessor
    {
        public async Task<WebhookProcessingResult> ProcessAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default)
        {
            probe.StatementTimeout = await db.Database.SqlQuery<string>($"SELECT current_setting('statement_timeout') AS \"Value\"").SingleAsync(cancellationToken);
            probe.IdleTimeout = await db.Database.SqlQuery<string>($"SELECT current_setting('idle_in_transaction_session_timeout') AS \"Value\"").SingleAsync(cancellationToken);
            return new WebhookProcessingResult(0, 0, 0, 0, 0);
        }
    }
}
