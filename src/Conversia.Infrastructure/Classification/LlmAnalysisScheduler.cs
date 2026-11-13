using System.Collections.Concurrent;
using Conversia.Application.Ingestion;
using Conversia.Application.Classification.Scheduling;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Conversia.Infrastructure.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Classification;

/// <summary>
/// Decides which companies are analysed now and drives the <see cref="LlmAnalysisRunner"/> (docs/CLASSIFICATION.md, "When the AI reads"). One
/// pass: release batches a crash left half prepared, poll the submitted batches, find the companies with the AI on, conversations pending and
/// an open window (their own schedule), then analyse them FAIRLY: one conversation per company in turn (round robin), at most
/// <c>Classification:Schedule:MaxConversationsPerWindow</c> per company and window, the companies with the fewest analysed in this window
/// first. At most <c>Classification:Llm:MaxConcurrent</c> analyses are in flight in this process: the permit is taken BEFORE the claim
/// (design D20), so a worker never holds a lease it cannot use. Nightly companies go in Message Batches when batch mode is on.
/// </summary>
public sealed class LlmAnalysisScheduler : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ClassificationOptions _options;
    private readonly LlmAnalysisRunner _runner;
    private readonly TimeProvider _time;
    private readonly ILogger<LlmAnalysisScheduler> _logger;
    private readonly SemaphoreSlim _permits;
    private DateTime _lastPoll = DateTime.MinValue;

    public LlmAnalysisScheduler(
        IServiceScopeFactory scopeFactory, ClassificationOptions options, LlmAnalysisRunner runner, TimeProvider time, ILogger<LlmAnalysisScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _runner = runner;
        _time = time;
        _logger = logger;
        var llm = options.Llm ?? throw new InvalidOperationException("The LLM scheduler needs Classification:Provider=Llm.");
        _permits = new SemaphoreSlim(llm.MaxConcurrent, llm.MaxConcurrent);
        MaxConcurrent = llm.MaxConcurrent;
    }

    /// <summary>The process-wide bound of analyses in flight (<c>Classification:Llm:MaxConcurrent</c>).</summary>
    public int MaxConcurrent { get; }

    /// <summary>A company due now: its open window and how many more conversations it may have analysed in it.</summary>
    public sealed record DueTenant(TenantWork Work, int Remaining);

    /// <summary>One pass. Returns the number of conversations handled (analysed, skipped, deferred, discarded or batched) and results collected.</summary>
    public async Task<int> RunPassAsync(CancellationToken cancellationToken = default)
    {
        var llm = _options.Llm!;
        var handled = 0;
        await _runner.RecoverAbandonedBatchesAsync(cancellationToken);

        var now = _time.GetUtcNow().UtcDateTime;
        if (_runner.BatchMode && now - _lastPoll >= TimeSpan.FromSeconds(llm.BatchPollSeconds))
        {
            _lastPoll = now;
            handled += await _runner.PollBatchesAsync(cancellationToken);
        }

        var due = await LoadDueTenantsAsync(now, cancellationToken);
        var batched = _runner.BatchMode ? due.Where(d => d.Work.Mode == AiAnalysisMode.Nightly).ToList() : [];
        var synchronous = due.Except(batched).ToList();

        handled += await RunSynchronousAsync(synchronous, cancellationToken);
        handled += await RunBatchAsync(batched, cancellationToken);

        if (handled == 0 && await _runner.WritePendingProfileAsync(cancellationToken))
        {
            handled++;
        }

        return handled;
    }

    /// <summary>
    /// The companies with the AI on, active, with pending conversations and an open window now, fewest analysed in the window first. Admission
    /// (quota, ceilings, caps) is the coordinator's, per conversation.
    /// </summary>
    public async Task<IReadOnlyList<DueTenant>> LoadDueTenantsAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var settings = _options.ScheduleSettings;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var candidates = await db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active && t.AiEnabled && db.Conversations.IgnoreQueryFilters().Any(c => c.TenantId == t.Id && c.LlmPending))
            .Select(t => new { t.Id, t.AiAnalysisMode, t.AiAnalysisNightlyHour, t.AiAnalysisIntervalHours })
            .ToListAsync(cancellationToken);

        var open = new List<(TenantWork Work, DateTime OpensAt)>();
        foreach (var t in candidates)
        {
            var window = AnalysisSchedule.WindowAt(new TenantAnalysisSchedule(t.AiAnalysisMode, t.AiAnalysisNightlyHour, t.AiAnalysisIntervalHours), t.Id, now, settings);
            if (window.IsOpenAt(now))
            {
                open.Add((new TenantWork(t.Id, t.AiAnalysisMode, window), window.OpensAt));
            }
        }

        if (open.Count == 0)
        {
            return [];
        }

        var ids = open.Select(o => o.Work.TenantId).ToList();
        var starts = open.Select(o => o.Work.Window.Start).Distinct().ToList();
        var runs = await db.AiAnalysisRuns.IgnoreQueryFilters().AsNoTracking()
            .Where(r => ids.Contains(r.TenantId) && starts.Contains(r.WindowStart))
            .Select(r => new { r.TenantId, r.WindowStart, r.Calls })
            .ToListAsync(cancellationToken);

        // The cap counts calls made and requests batched: deferrals of an open breaker or unchanged conversations cost nothing and do not
        // use up the company's window.
        return open
            .Select(o => (o, Calls: runs.FirstOrDefault(r => r.TenantId == o.Work.TenantId && r.WindowStart == o.Work.Window.Start)?.Calls ?? 0))
            .Where(x => x.Calls < settings.MaxConversationsPerWindow)
            .OrderBy(x => x.Calls).ThenBy(x => x.o.OpensAt).ThenBy(x => x.o.Work.TenantId)
            .Select(x => new DueTenant(x.o.Work, settings.MaxConversationsPerWindow - x.Calls))
            .ToList();
    }

    private async Task<int> RunSynchronousAsync(IReadOnlyList<DueTenant> tenants, CancellationToken cancellationToken)
    {
        if (tenants.Count == 0)
        {
            return 0;
        }

        // Round robin: a company goes back to the end of the queue after each conversation, so one company with a large backlog cannot
        // starve the others. Each company has up to Classification:Llm:PerCompanyConcurrency tokens in the queue (a few of its conversations
        // at once), and its remaining calls of the window are shared by its tokens.
        var perCompany = Math.Max(1, _options.Llm!.PerCompanyConcurrency);
        var remaining = new ConcurrentDictionary<Guid, int>(tenants.ToDictionary(t => t.Work.TenantId, t => t.Remaining));
        var queue = new ConcurrentQueue<TenantWork>();
        for (var round = 0; round < perCompany; round++)
        {
            foreach (var tenant in tenants.Where(t => t.Remaining > round))
            {
                queue.Enqueue(tenant.Work);
            }
        }

        var handled = 0;
        var workers = Enumerable.Range(0, Math.Max(1, _options.Llm!.Workers)).Select(_ => Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested && queue.TryDequeue(out var next))
            {
                if (remaining[next.TenantId] <= 0)
                {
                    continue;
                }

                await _permits.WaitAsync(cancellationToken);
                LlmStep step;
                try
                {
                    step = await _runner.ProcessNextAsync(next, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("The LLM analysis of a conversation of tenant {TenantId} failed: {Error}", next.TenantId, ClassificationErrors.Describe(ex));
                    continue; // this token is dropped: the company waits for the next pass
                }
                finally
                {
                    _permits.Release();
                }

                if (step == LlmStep.NothingDue)
                {
                    continue;
                }

                Interlocked.Increment(ref handled);
                if (step != LlmStep.Called || remaining.AddOrUpdate(next.TenantId, 0, (_, left) => left - 1) > 0)
                {
                    queue.Enqueue(next);
                }
            }
        }, cancellationToken)).ToList();

        await Task.WhenAll(workers);
        return handled;
    }

    private async Task<int> RunBatchAsync(IReadOnlyList<DueTenant> tenants, CancellationToken cancellationToken)
    {
        if (tenants.Count == 0)
        {
            return 0;
        }

        var llm = _options.Llm!;
        var queue = new Queue<DueTenant>(tenants);
        var assembly = new LlmBatchAssembly();
        var handled = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested && queue.TryDequeue(out var next))
            {
                await _permits.WaitAsync(cancellationToken);
                LlmStep step;
                try
                {
                    step = await _runner.PlanBatchItemAsync(next.Work, assembly, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Planning a batch request of tenant {TenantId} failed: {Error}", next.Work.TenantId, ClassificationErrors.Describe(ex));
                    continue;
                }
                finally
                {
                    _permits.Release();
                }

                if (step != LlmStep.NothingDue)
                {
                    handled++;
                    var left = step == LlmStep.Called ? next.Remaining - 1 : next.Remaining;
                    if (left > 0)
                    {
                        queue.Enqueue(next with { Remaining = left });
                    }
                }

                if (assembly.Count >= llm.BatchMaxRequests)
                {
                    await _runner.SubmitBatchAsync(assembly, cancellationToken);
                    assembly = new LlmBatchAssembly();
                }
            }
        }
        finally
        {
            // What was assembled is sent once (an assembly already attempted is never resent), within a bounded part of the host's
            // shutdown: a cut upload leaves the batch to recovery, which adopts it from the provider's list or fails it.
            using var upload = new CancellationTokenSource(ShutdownUploadBudget);
            await _runner.SubmitBatchAsync(assembly, upload.Token);
        }

        return handled;
    }

    /// <summary>How long the last upload of a pass may take once the host stops (the default host shutdown is 30 s).</summary>
    public static readonly TimeSpan ShutdownUploadBudget = TimeSpan.FromSeconds(20);

    public void Dispose() => _permits.Dispose();
}

/// <summary>Runs the <see cref="LlmAnalysisScheduler"/> in the background (only with <c>Classification:Provider=Llm</c> and the worker enabled).</summary>
public sealed class LlmAnalysisService(LlmAnalysisScheduler scheduler, ClassificationOptions options, ILogger<LlmAnalysisService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield();
            var failures = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    var handled = await scheduler.RunPassAsync(stoppingToken);
                    failures = 0;
                    wait = handled == 0 ? options.PollInterval : TimeSpan.Zero;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    failures++;
                    wait = WebhookWorkerBackoff.FailureDelay(failures);
                    logger.LogError(
                        "The LLM analysis pass failed ({Failures} in a row); next try in {Delay:0.#} s. Is the database reachable and migrated? {Error}",
                        failures, wait.TotalSeconds, WebhookErrors.Describe(ex));
                }

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, stoppingToken);
                }
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }
}
