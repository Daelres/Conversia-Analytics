using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Classification.Scheduling;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Classification;
using Conversia.Infrastructure.Classification.Usage;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The scheduler (which companies, when, how many at once, in what order) and batch mode (Message Batches through a fake batch client: submit,
/// partial results, errors, expiry, a restart that resumes, the discount in the usage meter).
/// </summary>
public partial class LlmClassificationTests
{
    /// <summary>
    /// The scheduler looks at every company with the AI on: conversations other test classes left pending would be analysed too. Only the
    /// companies of the calling test keep theirs.
    /// </summary>
    private async Task OnlyTheseCompaniesArePendingAsync(params Guid[] tenants)
    {
        await ExecuteAsync($"UPDATE \"Conversations\" SET \"LlmPending\" = false WHERE \"LlmPending\" AND NOT (\"TenantId\" = ANY({tenants}))");
        // Batches other tests left open would be polled, recovered or adopted by this test's runner and fake provider.
        await ExecuteAsync($"""
            UPDATE "LlmBatches" b SET "Status" = 'Collected', "CollectedAt" = now()
            WHERE b."Status" IN ('Preparing', 'Submitted', 'Ended')
              AND NOT EXISTS (SELECT 1 FROM "LlmBatchRequests" r WHERE r."BatchId" = b."Id" AND r."TenantId" = ANY({tenants}))
            """);
        // ... and their unresolved requests would be reserved in this test's admission.
        await ExecuteAsync($"""
            UPDATE "LlmBatchRequests" SET "Status" = 'Failed' WHERE "Status" IN ('Pending', 'Collecting') AND NOT ("TenantId" = ANY({tenants}))
            """);
    }

    private async Task SwitchOnAsync(Guid tenantId, AiAnalysisMode mode = AiAnalysisMode.NearRealTime, int? hour = null, int? interval = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.SetAiEnabled(true, "v-test", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        tenant.SetAiAnalysisSchedule(mode, hour, interval);
        await db.SaveChangesAsync();
    }

    /// <summary>A new customer message, as ingestion stores it (the snapshot changes) and requests the analysis again.</summary>
    private async Task AddCustomerMessageAsync(Guid conversationId, DateTime at, string text)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conversation = await db.Conversations.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == conversationId);
            db.Messages.Add(Message.Create(conversation.TenantId, conversationId, conversation.ChannelId, $"wamid.{Guid.NewGuid():N}", MessageDirection.Inbound, "text", text, at));
            await db.SaveChangesAsync();
        }

        await RequestAgainAsync(conversationId, at);
    }

    private LlmAnalysisScheduler Scheduler(LlmAnalysisRunner runner, TimeProvider clock, ClassificationOptions options)
        => new(_factory.Services.GetRequiredService<IServiceScopeFactory>(), options, runner, clock, NullLogger<LlmAnalysisScheduler>.Instance);

    private async Task<ChannelFixture> CompanyWithAsync(int conversations, DateTime start, AiAnalysisMode mode = AiAnalysisMode.NearRealTime, int? hour = null, int? interval = null)
    {
        var f = await StartChannelAsync(_factory, _client);
        for (var i = 0; i < conversations; i++)
        {
            await SeedPendingAsync(f, start.AddSeconds(i), PricedChat, requestClassification: true);
        }

        await SwitchOnAsync(f.TenantId, mode, hour, interval);
        return f;
    }

    // ---- When: the windows of each company ---------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyCompaniesWithTheAiOn_AndAnOpenWindow_AreDue()
    {
        var start = NewEra();
        using var nightly = await CompanyWithAsync(1, start, AiAnalysisMode.Nightly, hour: 1);
        using var off = await CompanyWithAsync(1, start);
        await ExecuteAsync($"UPDATE \"Tenants\" SET \"AiEnabled\" = false WHERE \"Id\" = {off.TenantId}");
        var options = LlmRunnerOptions();
        var scheduler = Scheduler(LlmRunner(new FixedTimeProvider(start), new FakeModel(), options: options), new FixedTimeProvider(start), options);
        var day = start.Date.AddDays(2);

        var at0030 = await scheduler.LoadDueTenantsAsync(day.AddHours(5).AddMinutes(30)); // 00:30 Bogota: the night window is not open yet
        var at0130 = await scheduler.LoadDueTenantsAsync(day.AddHours(6).AddMinutes(30)); // 01:30 Bogota: open
        var at0700 = await scheduler.LoadDueTenantsAsync(day.AddHours(12));                // 07:00 Bogota: closed again

        Assert.DoesNotContain(at0030, d => d.Work.TenantId == nightly.TenantId);
        var due = Assert.Single(at0130, d => d.Work.TenantId == nightly.TenantId);
        Assert.Equal((AiAnalysisMode.Nightly, day.AddHours(6), day.AddHours(11)), (due.Work.Mode, due.Work.Window.Start, due.Work.Window.End));
        Assert.DoesNotContain(at0700, d => d.Work.TenantId == nightly.TenantId);
        Assert.DoesNotContain(at0130, d => d.Work.TenantId == off.TenantId);
    }

    [Fact]
    public async Task APass_AnalysesEachCompanyOncePerWindow_EvenWhenMessagesKeepArriving()
    {
        var start = NewEra();
        using var f = await CompanyWithAsync(1, start, AiAnalysisMode.EveryHours, interval: 6);
        await OnlyTheseCompaniesArePendingAsync(f.TenantId);
        var model = new FakeModel();
        var options = LlmRunnerOptions();
        var clock = new FixedTimeProvider(start.AddDays(1)); // 07:00 Bogota: in the 06-12 window
        var runner = LlmRunner(clock, model, options: options);
        var scheduler = Scheduler(runner, clock, options);
        var conversation = (await LoadAsync(_factory.Services, (await FirstConversationAsync(f.TenantId)))).Conversation;

        await scheduler.RunPassAsync();
        for (var i = 1; i <= 3; i++)
        {
            await AddCustomerMessageAsync(conversation.Id, clock.GetUtcNow().UtcDateTime.AddMinutes(-10), $"otra pregunta {i}"); // quiet since
            clock.Advance(TimeSpan.FromMinutes(20));
            await scheduler.RunPassAsync();
        }

        Assert.Equal(1, model.Calls); // still the 06-12 window
        clock.Advance(TimeSpan.FromHours(5)); // 13:00 Bogota: the 12-18 window
        await scheduler.RunPassAsync();
        Assert.Equal(2, model.Calls);
    }

    private async Task<Guid> FirstConversationAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Conversations.IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId).OrderBy(c => c.StartedAt).Select(c => c.Id).FirstAsync();
    }

    // ---- How many at once, in what order ------------------------------------------------------------------------------------

    [Fact]
    public async Task NoMoreThanMaxConcurrentCalls_AreInFlight_WhateverTheWorkers()
    {
        var start = NewEra();
        using var a = await CompanyWithAsync(3, start);
        using var b = await CompanyWithAsync(3, start);
        using var c = await CompanyWithAsync(3, start);
        await OnlyTheseCompaniesArePendingAsync(a.TenantId, b.TenantId, c.TenantId);
        // The first two calls wait for each other: that proves two run at once; a third would find the barrier open and is counted.
        var two = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new FakeModel();
        model.Hook = async ct =>
        {
            if (model.MaxInFlight >= 2)
            {
                two.TrySetResult();
            }

            await two.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        };
        var options = LlmRunnerOptions() with { Llm = LlmRunnerOptions().Llm! with { MaxConcurrent = 2, Workers = 6 } };
        var clock = new FixedTimeProvider(start.AddDays(1));

        var handled = await Scheduler(LlmRunner(clock, model, options: options), clock, options).RunPassAsync();

        Assert.Equal((9, 9), (handled, model.Calls));
        Assert.Equal(2, model.MaxInFlight);
    }

    [Fact]
    public async Task OneCompanysBacklog_DoesNotStarveTheOthers_AndThePerWindowCapHolds()
    {
        var start = NewEra();
        using var big = await CompanyWithAsync(6, start);
        using var small = await CompanyWithAsync(1, start.AddHours(1)); // newer requests: a "oldest first" order would serve it last
        await OnlyTheseCompaniesArePendingAsync(big.TenantId, small.TenantId);
        var order = new ConcurrentQueue<Guid>();
        var model = new FakeModel();
        var options = LlmRunnerOptions() with
        {
            Llm = LlmRunnerOptions().Llm! with { MaxConcurrent = 1, Workers = 1 },
            Schedule = LlmRunnerOptions().ScheduleSettings with { MaxConversationsPerWindow = 4 },
        };
        var clock = new FixedTimeProvider(start.AddDays(1));
        var meter = new RecordingMeter(order);

        await Scheduler(LlmRunner(clock, model, options: options, meter: meter), clock, options).RunPassAsync();

        var calls = order.ToList();
        Assert.Contains(small.TenantId, calls.Take(2)); // round robin: the small company is served within the first turn
        Assert.Equal(4, calls.Count(t => t == big.TenantId)); // at most 4 per window
        Assert.Equal(2, await PendingCountAsync(big.TenantId)); // the rest waits for the next window
    }

    /// <summary>Admits everything and records which company each call was for, in order.</summary>
    private sealed class RecordingMeter(ConcurrentQueue<Guid> order) : IAiUsageMeter
    {
        public List<AiUsageRecord> Successes { get; } = [];

        public List<AiUsageRecord> Failures { get; } = [];

        public Task<AiAdmission> CheckAdmissionAsync(Guid tenantId, bool isSandbox, CancellationToken cancellationToken = default) => Task.FromResult(AiAdmission.Allowed);

        public Task RecordSuccessAsync(AiUsageRecord record, CancellationToken cancellationToken = default)
        {
            order.Enqueue(record.TenantId);
            lock (Successes)
            {
                Successes.Add(record);
            }

            return Task.CompletedTask;
        }

        public Task RecordFailureAsync(AiUsageRecord record, CancellationToken cancellationToken = default)
        {
            lock (Failures)
            {
                Failures.Add(record);
            }

            return Task.CompletedTask;
        }
    }

    private async Task<int> PendingCountAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Conversations.IgnoreQueryFilters()
            .CountAsync(c => c.TenantId == tenantId && c.LlmPending);
    }

    // ---- Batch mode ---------------------------------------------------------------------------------------------------------

    /// <summary>A fake Message Batches API: records the submissions; answers polls and results as the test sets them.</summary>
    private sealed class FakeBatches : ILlmBatchClient
    {
        private int _next;

        public ConcurrentDictionary<string, IReadOnlyList<LlmBatchItem>> Submitted { get; } = new();

        /// <summary>When each batch was created at the provider (for the list).</summary>
        public ConcurrentDictionary<string, DateTime> CreatedAt { get; } = new();

        /// <summary>Batches the provider has that are none of ours (another process's orphan): id, created, requests.</summary>
        public List<LlmBatchSummary> Others { get; } = [];

        public ConcurrentBag<string> Canceled { get; } = [];

        /// <summary>The provider's clock for created_at.</summary>
        public Func<DateTime> Now { get; set; } = () => new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Runs inside the upload, after the provider created the batch (a concurrent failure, a crash before the id is saved).</summary>
        public Func<string, Task>? DuringSubmit { get; set; }

        /// <summary>Polling fails (404, results gone).</summary>
        public Exception? GetFails { get; set; }

        public DateTime? ExpiresAt { get; set; }

        public Exception? SubmitFails { get; set; }

        public LlmBatchProcessing Processing { get; set; } = LlmBatchProcessing.InProgress;

        /// <summary>The result stream breaks (a dropped connection, a crash) after this many results.</summary>
        public int? BreakAfter { get; set; }

        /// <summary>The result of each request (default: a usable answer).</summary>
        public Func<LlmBatchItem, LlmBatchResult?> Result { get; set; } = item => new(item.CustomId, LlmBatchResultKind.Succeeded,
            new LlmCompletion(LlmStopReason.EndTurn, Json("in_follow_up"), new LlmUsage(1000, 100, 0, 0), "claude-haiku-5-5"), null);

        public async Task<LlmBatchSubmission> SubmitAsync(IReadOnlyList<LlmBatchItem> items, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (SubmitFails is { } ex)
            {
                throw ex;
            }

            var id = $"msgbatch_fake{Interlocked.Increment(ref _next)}_{Guid.NewGuid():N}";
            Submitted[id] = items;
            CreatedAt[id] = Now();
            if (DuringSubmit is { } hook)
            {
                await hook(id);
            }

            return new LlmBatchSubmission(id, ExpiresAt ?? new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        public Task<LlmBatchState> GetAsync(string providerBatchId, CancellationToken cancellationToken = default)
            => GetFails is { } fails ? Task.FromException<LlmBatchState>(fails) : Task.FromResult(new LlmBatchState(Processing, 0, Submitted[providerBatchId].Count, 0, 0, 0, null));

        public async IAsyncEnumerable<LlmBatchSummary> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            var all = Submitted.Select(b => new LlmBatchSummary(b.Key, CreatedAt[b.Key], Processing, b.Value.Count)).Concat(Others);
            foreach (var batch in all.OrderByDescending(b => b.CreatedAt))
            {
                yield return batch;
            }
        }

        public Task CancelAsync(string providerBatchId, CancellationToken cancellationToken = default)
        {
            Canceled.Add(providerBatchId);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<LlmBatchResult> ResultsAsync(string providerBatchId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            var sent = 0;
            foreach (var item in Submitted[providerBatchId])
            {
                if (BreakAfter is { } limit && sent++ >= limit)
                {
                    throw new LlmCallException(LlmFailureKind.Unavailable, "AnthropicIOException");
                }

                if (Result(item) is { } result)
                {
                    yield return result;
                }
            }
        }
    }

    private static ClassificationOptions BatchOptions() => LlmRunnerOptions() with
    {
        Llm = LlmRunnerOptions().Llm! with { BatchEnabled = true, BatchPollSeconds = 1 },
    };

    private async Task<List<LlmBatchRequest>> BatchRequestsAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().LlmBatchRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tenantId).OrderBy(r => r.CreatedAt).ToListAsync();
    }

    private async Task<LlmBatch> BatchAsync(Guid batchId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().LlmBatches.AsNoTracking().SingleAsync(b => b.Id == batchId);
    }

    /// <summary>A nightly company (07:00 Bogota = the era's 12:00 UTC, so its window is open the next day at 12:00 UTC) with its conversations.</summary>
    private async Task<(ChannelFixture Company, FixedTimeProvider Clock)> NightlyCompanyAsync(int conversations)
    {
        var start = NewEra();
        var f = await CompanyWithAsync(conversations, start, AiAnalysisMode.Nightly, hour: 7);
        await OnlyTheseCompaniesArePendingAsync(f.TenantId);
        return (f, new FixedTimeProvider(start.AddDays(1)));
    }

    [Fact]
    public async Task ANightlyWindow_GoesInOneBatch_WhoseResultsAreAppliedWithTheDiscount_AndAnErroredRequestFallsBackToASynchronousCall()
    {
        var (f, clock) = await NightlyCompanyAsync(2);
        var model = new FakeModel { Answer = _ => Json("in_follow_up", services: [OtherService]) };
        var batches = new FakeBatches();
        var meter = new AiUsageMeter(_factory.Services.GetRequiredService<IServiceScopeFactory>(), AiQuotaSettings.Default, clock, NullLogger<AiUsageMeter>.Instance);
        var options = BatchOptions();
        var runner = LlmRunner(clock, model, options: options, meter: meter, batches: batches);
        var scheduler = Scheduler(runner, clock, options);

        // Pass 1: both conversations go in one batch; nothing is called synchronously, nothing is applied yet.
        await scheduler.RunPassAsync();
        var (providerId, items) = Assert.Single(batches.Submitted);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Matches("^c[0-9a-f]{32}-[0-9a-f]{30}$", i.CustomId));
        Assert.Equal(0, model.Calls);
        var requests = await BatchRequestsAsync(f.TenantId);
        Assert.All(requests, r => Assert.Equal(LlmBatchRequestStatus.Pending, r.Status));
        var batch = await BatchAsync(requests[0].BatchId);
        Assert.Equal((LlmBatchStatus.Submitted, providerId, 2), (batch.Status, batch.ProviderBatchId, batch.RequestCount));
        var leased = (await LoadAsync(_factory.Services, requests[0].ConversationId)).Conversation;
        Assert.Equal((true, (DateTime?)clock.GetUtcNow().UtcDateTime.Add(LlmOptions.BatchLease)), (leased.LlmPending, leased.LlmLeaseUntil));

        // Still processing: nothing happens.
        clock.Advance(TimeSpan.FromSeconds(5));
        await scheduler.RunPassAsync();
        Assert.Equal(LlmBatchStatus.Submitted, (await BatchAsync(batch.Id)).Status);

        // Ended: the first request succeeded, the second errored.
        batches.Processing = LlmBatchProcessing.Ended;
        var erroredId = requests[1].CustomId;
        var succeeded = batches.Result;
        batches.Result = item => item.CustomId == erroredId ? new(item.CustomId, LlmBatchResultKind.Errored, null, "api_error") : succeeded(item);
        clock.Advance(TimeSpan.FromSeconds(5));
        await scheduler.RunPassAsync();

        var first = await LoadAsync(_factory.Services, requests[0].ConversationId);
        Assert.Equal((false, (DateTime?)null), (first.Conversation.LlmPending, first.Conversation.LlmLeaseUntil));
        Assert.Equal(OpportunityStage.InFollowUp, first.Opportunity.Stage);
        Assert.Equal(LlmBatchStatus.Collected, (await BatchAsync(batch.Id)).Status);
        var resolved = await BatchRequestsAsync(f.TenantId);
        Assert.Equal((LlmBatchRequestStatus.Applied, LlmBatchRequestStatus.Errored), (resolved[0].Status, resolved[1].Status));

        // The usage is recorded: the batched answer with the discount.
        using (var scope = _factory.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AiUsageMonths.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(u => u.TenantId == f.TenantId);
            // The batched answer at half the list price; the synchronous fallback (below) at the full price.
            Assert.Equal((2, 2, 1000L + 1200L), (row.ConversationsAnalyzed, row.Calls, row.InputTokens));
            Assert.Equal(
                (LlmPricing.Default.Estimate(new LlmUsage(1000, 100, 0, 0)) * 0.5m) + LlmPricing.Default.Estimate(new LlmUsage(1200, 80, 2900, 0)), row.EstimatedUsd);
        }

        // The errored one was released and goes the synchronous way in the same window (once only).
        Assert.Equal(1, model.Calls);
        var second = await LoadAsync(_factory.Services, requests[1].ConversationId);
        Assert.Equal((false, OtherService), (second.Conversation.LlmPending, second.Opportunity.ServiceInterest));
        var run = await RunOfAsync(f.TenantId, requests[0].WindowStart);
        Assert.Equal((2, 2, 1), (run!.Batched, run.Analysed, run.Deferred));
    }

    [Fact]
    public async Task ExpiredAndMissingResults_ReleaseTheConversation_ForTheSynchronousPath()
    {
        var (f, clock) = await NightlyCompanyAsync(2);
        var model = new FakeModel();
        var batches = new FakeBatches { Processing = LlmBatchProcessing.Ended };
        var options = BatchOptions();
        var runner = LlmRunner(clock, model, options: options, batches: batches);
        await Scheduler(runner, clock, options).RunPassAsync();
        var requests = await BatchRequestsAsync(f.TenantId);
        batches.Result = item => item.CustomId == requests[0].CustomId ? new(item.CustomId, LlmBatchResultKind.Expired, null, null) : null; // the second: no result at all

        await runner.PollBatchesAsync();

        var resolved = await BatchRequestsAsync(f.TenantId);
        Assert.All(resolved, r => Assert.Equal(LlmBatchRequestStatus.Expired, r.Status));
        foreach (var r in resolved)
        {
            var conversation = (await LoadAsync(_factory.Services, r.ConversationId)).Conversation;
            Assert.Equal((true, (DateTime?)null), (conversation.LlmPending, conversation.LlmLeaseUntil));
        }

        clock.Advance(TimeSpan.FromSeconds(5));
        await Scheduler(runner, clock, options).RunPassAsync();
        Assert.Equal(2, model.Calls); // both synchronously, not in a second batch
        Assert.Single(batches.Submitted);
    }

    [Fact]
    public async Task ARestart_ResumesPollingAndCollecting_WithoutApplyingAnythingTwice()
    {
        var (f, clock) = await NightlyCompanyAsync(2);
        var batches = new FakeBatches();
        var options = BatchOptions();
        await Scheduler(LlmRunner(clock, new FakeModel(), options: options, batches: batches), clock, options).RunPassAsync();
        var requests = await BatchRequestsAsync(f.TenantId);

        // The process dies in the middle of the collection: the result stream breaks after the first result.
        batches.Processing = LlmBatchProcessing.Ended;
        batches.BreakAfter = 1;
        var meter = new RecordingMeter(new ConcurrentQueue<Guid>());
        await LlmRunner(clock, new FakeModel(), options: options, meter: meter, batches: batches).PollBatchesAsync();
        Assert.Equal(LlmBatchStatus.Ended, (await BatchAsync(requests[0].BatchId)).Status);
        var halfway = await BatchRequestsAsync(f.TenantId);
        Assert.Single(halfway, r => r.Status == LlmBatchRequestStatus.Applied);
        Assert.Single(halfway, r => r.Status == LlmBatchRequestStatus.Pending);

        // A new process (new runner, same database) picks the batch up and collects the rest; the first result is not applied again.
        batches.BreakAfter = null;
        var restarted = new RecordingMeter(new ConcurrentQueue<Guid>());
        await LlmRunner(clock, new FakeModel(), options: options, meter: restarted, batches: batches).PollBatchesAsync();

        Assert.Single(meter.Successes);
        var resumed = Assert.Single(restarted.Successes);
        Assert.NotEqual(meter.Successes[0].ConversationId, resumed.ConversationId);
        Assert.Equal(LlmBatchStatus.Collected, (await BatchAsync(requests[0].BatchId)).Status);
        Assert.All(await BatchRequestsAsync(f.TenantId), r => Assert.Equal(LlmBatchRequestStatus.Applied, r.Status));
    }

    [Fact]
    public async Task ABatchAnswerForAConversationThatChangedMeanwhile_IsDiscarded_ButItsCostIsRecorded()
    {
        var (f, clock) = await NightlyCompanyAsync(1);
        var batches = new FakeBatches();
        var options = BatchOptions();
        var meter = new RecordingMeter(new ConcurrentQueue<Guid>());
        var runner = LlmRunner(clock, new FakeModel(), options: options, meter: meter, batches: batches);
        await Scheduler(runner, clock, options).RunPassAsync();
        var request = Assert.Single(await BatchRequestsAsync(f.TenantId));

        // The customer writes again while the batch is processed.
        await AddCustomerMessageAsync(request.ConversationId, clock.GetUtcNow().UtcDateTime, "mejor mañana te confirmo");
        batches.Processing = LlmBatchProcessing.Ended;

        await runner.PollBatchesAsync();

        Assert.Equal(LlmBatchRequestStatus.Discarded, Assert.Single(await BatchRequestsAsync(f.TenantId)).Status);
        var conversation = (await LoadAsync(_factory.Services, request.ConversationId)).Conversation;
        Assert.Equal((true, (DateTime?)null, (string?)null), (conversation.LlmPending, conversation.LlmLeaseUntil, conversation.LastLlmSnapshotHash));
        Assert.Empty(meter.Successes);
        Assert.Equal(LlmPricing.Default.Estimate(new LlmUsage(1000, 100, 0, 0)) * 0.5m, Assert.Single(meter.Failures).EstimatedUsd);

        // Still in the window: it is read again once, synchronously (not in a second batch), and the reading is applied.
        var model = new FakeModel();
        clock.Advance(TimeSpan.FromMinutes(6));
        await Scheduler(LlmRunner(clock, model, options: options, meter: meter, batches: batches), clock, options).RunPassAsync();
        Assert.Equal((1, 1), (model.Calls, batches.Submitted.Count));
        Assert.False((await LoadAsync(_factory.Services, request.ConversationId)).Conversation.LlmPending);
    }

    [Fact]
    public async Task ABatchThatCannotBeSent_OrWasAbandonedByACrash_ReleasesItsConversations()
    {
        var (f, clock) = await NightlyCompanyAsync(1);
        var batches = new FakeBatches { SubmitFails = new LlmCallException(LlmFailureKind.Unavailable, "Anthropic5xxException") };
        var options = BatchOptions();
        var model = new FakeModel();
        var runner = LlmRunner(clock, model, options: options, batches: batches);

        await Scheduler(runner, clock, options).RunPassAsync();

        var request = Assert.Single(await BatchRequestsAsync(f.TenantId));
        var failed = await BatchAsync(request.BatchId);
        Assert.Equal((LlmBatchStatus.Failed, "Unavailable:Anthropic5xxException", LlmBatchRequestStatus.Errored), (failed.Status, failed.Error, request.Status));
        Assert.Null((await LoadAsync(_factory.Services, request.ConversationId)).Conversation.LlmLeaseUntil);
        await Scheduler(runner, clock, options).RunPassAsync();
        Assert.Equal(1, model.Calls); // the synchronous fallback

        // A batch left "Preparing" by a crash (rows written, never sent) is failed after 10 minutes, its conversations released.
        var (g, later) = await NightlyCompanyAsync(1);
        var conversationId = await FirstConversationAsync(g.TenantId);
        var abandoned = LlmBatch.Prepare(1, later.GetUtcNow().UtcDateTime);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LlmBatches.Add(abandoned);
            db.LlmBatchRequests.Add(LlmBatchRequest.For(abandoned.Id, g.TenantId, conversationId, later.GetUtcNow().UtcDateTime, new string('d', 64), false, null,
                OpportunityStage.New, null, later.GetUtcNow().UtcDateTime, leaseUntil: later.GetUtcNow().UtcDateTime.AddHours(25)));
            await db.SaveChangesAsync();
        }

        await ExecuteAsync($"UPDATE \"Conversations\" SET \"LlmLeaseUntil\" = {later.GetUtcNow().UtcDateTime.AddHours(25)} WHERE \"Id\" = {conversationId}");
        var recovering = LlmRunner(later, new FakeModel(), options: options, batches: new FakeBatches());
        Assert.Equal(0, await recovering.RecoverAbandonedBatchesAsync());
        later.Advance(TimeSpan.FromMinutes(11));
        Assert.True(await recovering.RecoverAbandonedBatchesAsync() >= 1);
        Assert.Equal((LlmBatchStatus.Failed, "abandoned"), ((await BatchAsync(abandoned.Id)).Status, (await BatchAsync(abandoned.Id)).Error));
        Assert.Null((await LoadAsync(_factory.Services, conversationId)).Conversation.LlmLeaseUntil);
    }

    // ---- Review round: orphans, ownership, poison batches, caps, reservations, retention, suppression ----------------------------

    /// <summary>The state a crash (or a failed save) right after the provider created the batch leaves: the batch exists there, not here.</summary>
    private async Task LoseTheProviderIdAsync(Guid batchId, DateTime uploadStartedAt)
        => await ExecuteAsync($"""
            UPDATE "LlmBatches" SET "Status" = 'Preparing', "ProviderBatchId" = NULL, "SubmittedAt" = NULL, "SubmitStartedAt" = {uploadStartedAt}
            WHERE "Id" = {batchId}
            """);

    [Fact]
    public async Task ABatchWhoseIdWasNeverSaved_IsAdoptedOnRecovery_NotAnalysedASecondTimeSynchronously()
    {
        var (f, clock) = await NightlyCompanyAsync(2);
        var batches = new FakeBatches { Now = () => clock.GetUtcNow().UtcDateTime };
        var options = BatchOptions();
        var model = new FakeModel();
        await Scheduler(LlmRunner(clock, model, options: options, batches: batches), clock, options).RunPassAsync();
        var requests = await BatchRequestsAsync(f.TenantId);
        var (providerId, _) = Assert.Single(batches.Submitted);
        await LoseTheProviderIdAsync(requests[0].BatchId, clock.GetUtcNow().UtcDateTime);

        clock.Advance(TimeSpan.FromMinutes(11));
        await Scheduler(LlmRunner(clock, model, options: options, batches: batches), clock, options).RunPassAsync();

        var adopted = await BatchAsync(requests[0].BatchId);
        Assert.Equal((LlmBatchStatus.Submitted, providerId), (adopted.Status, adopted.ProviderBatchId));
        Assert.Equal(0, model.Calls); // the provider is already processing them: no synchronous second analysis
        Assert.Single(batches.Submitted);
        Assert.Empty(batches.Canceled);

        batches.Processing = LlmBatchProcessing.Ended;
        clock.Advance(TimeSpan.FromSeconds(5));
        await Scheduler(LlmRunner(clock, model, options: options, batches: batches), clock, options).RunPassAsync();
        Assert.All(await BatchRequestsAsync(f.TenantId), r => Assert.Equal(LlmBatchRequestStatus.Applied, r.Status));
    }

    [Fact]
    public async Task AnUnknownProviderBatchWithNoMatch_IsCanceled_AndTheLocalBatchReleasesItsConversations()
    {
        var (f, clock) = await NightlyCompanyAsync(2);
        var batches = new FakeBatches { Now = () => clock.GetUtcNow().UtcDateTime };
        var options = BatchOptions();
        await Scheduler(LlmRunner(clock, new FakeModel(), options: options, batches: batches), clock, options).RunPassAsync();
        var requests = await BatchRequestsAsync(f.TenantId);
        var (ours, _) = Assert.Single(batches.Submitted);
        batches.Submitted.TryRemove(ours, out _); // the provider never got ours ...
        batches.Others.Add(new LlmBatchSummary("msgbatch_orphan", clock.GetUtcNow().UtcDateTime, LlmBatchProcessing.InProgress, 5)); // ... but has another one
        await LoseTheProviderIdAsync(requests[0].BatchId, clock.GetUtcNow().UtcDateTime);

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True(await LlmRunner(clock, new FakeModel(), options: options, batches: batches).RecoverAbandonedBatchesAsync() >= 1);

        Assert.Equal(["msgbatch_orphan"], batches.Canceled);
        Assert.Equal(LlmBatchStatus.Failed, (await BatchAsync(requests[0].BatchId)).Status);
        foreach (var r in requests)
        {
            Assert.Null((await LoadAsync(_factory.Services, r.ConversationId)).Conversation.LlmLeaseUntil);
        }
    }

    [Fact]
    public async Task ABatchFailedWhileItsUploadWasInFlight_StaysFailed_AndTheProviderBatchIsCanceled()
    {
        var (f, clock) = await NightlyCompanyAsync(1);
        var batches = new FakeBatches();
        batches.DuringSubmit = async _ => await ExecuteAsync($"UPDATE \"LlmBatches\" SET \"Status\" = 'Failed', \"Error\" = 'abandoned' WHERE \"Status\" = 'Preparing'");
        var options = BatchOptions();

        await Scheduler(LlmRunner(clock, new FakeModel(), options: options, batches: batches), clock, options).RunPassAsync();

        var request = Assert.Single(await BatchRequestsAsync(f.TenantId));
        Assert.Equal((LlmBatchStatus.Failed, (string?)null), ((await BatchAsync(request.BatchId)).Status, (await BatchAsync(request.BatchId)).ProviderBatchId));
        Assert.Equal(batches.Submitted.Keys.ToList(), batches.Canceled.ToList());
    }

    [Fact]
    public async Task ARequestACrashedCollectorLeftHalfway_IsReleased_WithoutRecordingItsUsageAgain()
    {
        var (f, clock) = await NightlyCompanyAsync(1);
        var batches = new FakeBatches();
        var options = BatchOptions();
        var meter = new RecordingMeter(new ConcurrentQueue<Guid>());
        await Scheduler(LlmRunner(clock, new FakeModel(), options: options, meter: meter, batches: batches), clock, options).RunPassAsync();
        var request = Assert.Single(await BatchRequestsAsync(f.TenantId));
        await ExecuteAsync($"UPDATE \"LlmBatchRequests\" SET \"Status\" = 'Collecting', \"CollectingAt\" = {clock.GetUtcNow().UtcDateTime} WHERE \"Id\" = {request.Id}");
        await ExecuteAsync($"UPDATE \"LlmBatches\" SET \"Status\" = 'Ended' WHERE \"Id\" = {request.BatchId}");
        batches.Processing = LlmBatchProcessing.Ended;

        clock.Advance(TimeSpan.FromMinutes(11));
        await LlmRunner(clock, new FakeModel(), options: options, meter: meter, batches: batches).PollBatchesAsync();

        Assert.Empty(meter.Successes);
        Assert.Empty(meter.Failures);
        Assert.Equal(LlmBatchRequestStatus.Failed, Assert.Single(await BatchRequestsAsync(f.TenantId)).Status);
        Assert.Equal(LlmBatchStatus.Collected, (await BatchAsync(request.BatchId)).Status);
        Assert.Null((await LoadAsync(_factory.Services, request.ConversationId)).Conversation.LlmLeaseUntil);
    }

    [Fact]
    public async Task TwoCollectorsAtOnce_ApplyAndRecordEachResultOnce()
    {
        var (f, clock) = await NightlyCompanyAsync(3);
        var batches = new FakeBatches();
        var options = BatchOptions();
        await Scheduler(LlmRunner(clock, new FakeModel(), options: options, batches: batches), clock, options).RunPassAsync();
        batches.Processing = LlmBatchProcessing.Ended;
        var meter = new RecordingMeter(new ConcurrentQueue<Guid>());

        await Task.WhenAll(
            LlmRunner(clock, new FakeModel(), options: options, meter: meter, batches: batches).PollBatchesAsync(),
            LlmRunner(clock, new FakeModel(), options: options, meter: meter, batches: batches).PollBatchesAsync());
        await LlmRunner(clock, new FakeModel(), options: options, meter: meter, batches: batches).PollBatchesAsync(); // whatever the lock left

        Assert.Equal(3, meter.Successes.Count);
        Assert.Equal(3, meter.Successes.Select(r => r.ConversationId).Distinct().Count());
        Assert.All(await BatchRequestsAsync(f.TenantId), r => Assert.Equal(LlmBatchRequestStatus.Applied, r.Status));
    }

    [Fact]
    public async Task ABatchThatCanNoLongerBePolled_IsFailedAfterItsExpiryAndAGrace_AndItsConversationsReleased()
    {
        var (f, clock) = await NightlyCompanyAsync(1);
        var batches = new FakeBatches { ExpiresAt = clock.GetUtcNow().UtcDateTime.AddHours(24) };
        var options = BatchOptions();
        await Scheduler(LlmRunner(clock, new FakeModel(), options: options, batches: batches), clock, options).RunPassAsync();
        var request = Assert.Single(await BatchRequestsAsync(f.TenantId));
        batches.GetFails = new LlmCallException(LlmFailureKind.Misconfigured, "model_not_found");

        clock.Advance(TimeSpan.FromHours(24 + 47));
        await LlmRunner(clock, new FakeModel(), options: options, batches: batches).PollBatchesAsync();
        Assert.Equal(LlmBatchStatus.Submitted, (await BatchAsync(request.BatchId)).Status); // within the grace: tried again later

        clock.Advance(TimeSpan.FromHours(2));
        await LlmRunner(clock, new FakeModel(), options: options, batches: batches).PollBatchesAsync();
        var poisoned = await BatchAsync(request.BatchId);
        Assert.Equal((LlmBatchStatus.Failed, "poison"), (poisoned.Status, poisoned.Error?.Split(':')[0]));
        Assert.Null((await LoadAsync(_factory.Services, request.ConversationId)).Conversation.LlmLeaseUntil);
    }

    [Fact]
    public async Task ThePerWindowCap_CountsCalls_NotDeferralsOfAnOpenBreaker()
    {
        var start = NewEra();
        using var f = await CompanyWithAsync(4, start);
        await OnlyTheseCompaniesArePendingAsync(f.TenantId);
        var model = new FakeModel { Hook = _ => throw new LlmCallException(LlmFailureKind.Unavailable, "Anthropic5xxException") };
        // One at a time, so exactly one call happens before the breaker opens.
        var options = LlmRunnerOptions() with
        {
            Llm = LlmRunnerOptions().Llm! with { MaxConcurrent = 1, Workers = 1, PerCompanyConcurrency = 1 },
            Schedule = LlmRunnerOptions().ScheduleSettings with { MaxConversationsPerWindow = 2 },
        };
        var clock = new FixedTimeProvider(start.AddDays(1));
        var runner = LlmRunner(clock, model, options: options, breakerFailures: 1);
        var scheduler = Scheduler(runner, clock, options);

        await scheduler.RunPassAsync(); // one call fails, the breaker opens, the other three are deferred without a call

        Assert.Equal(1, model.Calls);
        var due = Assert.Single(await scheduler.LoadDueTenantsAsync(clock.GetUtcNow().UtcDateTime), d => d.Work.TenantId == f.TenantId);
        Assert.Equal(1, due.Remaining);
        Assert.Equal(1, (await RunOfAsync(f.TenantId, due.Work.Window.Start))!.Calls);
    }

    [Fact]
    public async Task PendingBatchRequests_AreReservedInAdmission_SoABatchNeverGoesOverTheCompanysQuota()
    {
        var (f, clock) = await NightlyCompanyAsync(3);
        await ExecuteAsync($"UPDATE \"Tenants\" SET \"AiMonthlyConversationQuota\" = 2 WHERE \"Id\" = {f.TenantId}");
        var meter = new AiUsageMeter(_factory.Services.GetRequiredService<IServiceScopeFactory>(), AiQuotaSettings.Default, clock, NullLogger<AiUsageMeter>.Instance);
        var batches = new FakeBatches();
        var options = BatchOptions();

        await Scheduler(LlmRunner(clock, new FakeModel(), options: options, meter: meter, batches: batches), clock, options).RunPassAsync();

        Assert.Equal(2, Assert.Single(batches.Submitted).Value.Count);
        Assert.Equal(1, await PendingCountAsync(f.TenantId) - (await BatchRequestsAsync(f.TenantId)).Count); // the third waits, still pending
        Assert.All(await BatchRequestsAsync(f.TenantId), r => Assert.True(r.EstimatedUsd > 0m));
    }

    [Fact]
    public async Task PendingBatchRequests_CountAgainstTheDeploymentCap()
    {
        var (f, clock) = await NightlyCompanyAsync(3);
        // A cap below two requests' estimate: after the first is reserved, the deployment is full (whatever other tests spent before is in other months).
        var meter = new AiUsageMeter(_factory.Services.GetRequiredService<IServiceScopeFactory>(), AiQuotaSettings.Default with { DeploymentMonthlyUsdCap = 0.0001m }, clock,
            NullLogger<AiUsageMeter>.Instance);
        var batches = new FakeBatches();
        var options = BatchOptions();

        await Scheduler(LlmRunner(clock, new FakeModel(), options: options, meter: meter, batches: batches), clock, options).RunPassAsync();

        Assert.True(Assert.Single(batches.Submitted).Value.Count < 3);
    }

    [Fact]
    public async Task ResolvedBatchesOlderThanTheRetention_ArePurged_TheRestKept()
    {
        var (f, clock) = await NightlyCompanyAsync(2);
        var batches = new FakeBatches { Processing = LlmBatchProcessing.Ended };
        var options = BatchOptions();
        var runner = LlmRunner(clock, new FakeModel(), options: options, batches: batches);
        await Scheduler(runner, clock, options).RunPassAsync();
        await runner.PollBatchesAsync();
        var collected = (await BatchRequestsAsync(f.TenantId))[0].BatchId;
        var open = LlmBatch.Prepare(1, clock.GetUtcNow().UtcDateTime);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LlmBatches.Add(open);
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await runner.PurgeResolvedBatchesAsync());
        clock.Advance(TimeSpan.FromDays(31));
        Assert.True(await runner.PurgeResolvedBatchesAsync() >= 1);

        using var check = _factory.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await after.LlmBatches.AnyAsync(b => b.Id == collected));
        Assert.Empty(await BatchRequestsAsync(f.TenantId));
        Assert.True(await after.LlmBatches.AnyAsync(b => b.Id == open.Id)); // not resolved: kept
    }

    [Fact]
    public async Task ABatchAnswerForASuppressedConversation_LeavesNoTraceOfItsId_InTheUsageSets()
    {
        var (f, clock) = await NightlyCompanyAsync(1);
        var batches = new FakeBatches();
        var options = BatchOptions();
        var meter = new AiUsageMeter(_factory.Services.GetRequiredService<IServiceScopeFactory>(), AiQuotaSettings.Default, clock, NullLogger<AiUsageMeter>.Instance);
        var runner = LlmRunner(clock, new FakeModel(), options: options, meter: meter, batches: batches);
        await Scheduler(runner, clock, options).RunPassAsync();
        var request = Assert.Single(await BatchRequestsAsync(f.TenantId));
        // The contact is suppressed while the batch runs: its conversation and everything of it go (docs/USERS_TENANTS_PERMISSIONS.md).
        await ExecuteAsync($"DELETE FROM \"Messages\" WHERE \"ConversationId\" = {request.ConversationId}");
        await ExecuteAsync($"DELETE FROM \"Opportunities\" WHERE \"ConversationId\" = {request.ConversationId}");
        await ExecuteAsync($"DELETE FROM \"Conversations\" WHERE \"Id\" = {request.ConversationId}");
        batches.Processing = LlmBatchProcessing.Ended;

        await runner.PollBatchesAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.AiUsageConversations.IgnoreQueryFilters().AnyAsync(u => u.ConversationId == request.ConversationId));
        Assert.Equal(LlmBatchStatus.Collected, (await BatchAsync(request.BatchId)).Status);
    }

    [Fact]
    public async Task AFewConversationsOfOneCompany_AreReadAtOnce_UpToThePerCompanyConcurrency()
    {
        var start = NewEra();
        using var f = await CompanyWithAsync(4, start);
        await OnlyTheseCompaniesArePendingAsync(f.TenantId);
        var two = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new FakeModel();
        model.Hook = async ct =>
        {
            if (model.MaxInFlight >= 2)
            {
                two.TrySetResult();
            }

            await two.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        };
        var options = LlmRunnerOptions() with { Llm = LlmRunnerOptions().Llm! with { MaxConcurrent = 4, Workers = 4, PerCompanyConcurrency = 2 } };
        var clock = new FixedTimeProvider(start.AddDays(1));

        await Scheduler(LlmRunner(clock, model, options: options), clock, options).RunPassAsync();

        Assert.Equal((4, 2), (model.Calls, model.MaxInFlight));
    }
}
