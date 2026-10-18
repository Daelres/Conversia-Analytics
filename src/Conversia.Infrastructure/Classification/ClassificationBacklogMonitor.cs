using Conversia.Application.Ingestion;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Classification;

/// <param name="Waiting">Conversations flagged for classification that the worker will still try.</param>
/// <param name="OldestWaitingAge">How long the oldest request has been waiting; null when none.</param>
/// <param name="Stopped">Flagged conversations the worker gave up on after <c>MaxFailures</c> failures (until a new message arrives).</param>
public sealed record ClassificationBacklog(int Waiting, TimeSpan? OldestWaitingAge, int Stopped);

/// <summary>A few cheap counts over the classification backlog of all tenants (an operations view; it returns no customer data).</summary>
public sealed class ClassificationBacklogMonitor(IServiceScopeFactory scopeFactory, ClassificationOptions options, TimeProvider timeProvider)
{
    public async Task<ClassificationBacklog> GetBacklogAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var flagged = db.Conversations.IgnoreQueryFilters().AsNoTracking().Where(c => c.ClassificationDirty);
        var waiting = flagged.Where(c => c.ClassificationFailures < options.MaxFailures);
        var count = await waiting.CountAsync(cancellationToken);
        var oldest = count == 0 ? null : await waiting.MinAsync(c => c.ClassificationRequestedAt, cancellationToken);
        var stopped = await flagged.CountAsync(c => c.ClassificationFailures >= options.MaxFailures, cancellationToken);
        return new ClassificationBacklog(count, oldest is null ? null : now - oldest.Value, stopped);
    }
}

/// <summary>Logs the size of the classification backlog at Information, at most once per <see cref="Interval"/> however often it is asked.</summary>
public sealed class ClassificationBacklogReporter(ClassificationBacklogMonitor monitor, TimeProvider timeProvider, ILogger<ClassificationBacklogReporter> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private long? _lastReport;

    public async Task ReportIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (_lastReport is { } last && timeProvider.GetElapsedTime(last) < Interval)
        {
            return;
        }

        _lastReport = timeProvider.GetTimestamp(); // also after a failure: an unreachable database must not be asked (and logged) every cycle
        try
        {
            var backlog = await monitor.GetBacklogAsync(cancellationToken);
            logger.LogInformation(
                "Classification backlog: {Waiting} waiting, oldest for {OldestSeconds:0} s, {Stopped} stopped after repeated failures.",
                backlog.Waiting, backlog.OldestWaitingAge?.TotalSeconds ?? 0, backlog.Stopped);
            if (backlog.Stopped > 0)
            {
                logger.LogWarning("{Stopped} conversations are not being classified: their classification failed repeatedly.", backlog.Stopped);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The classification backlog could not be read: {Error}", WebhookErrors.Describe(ex));
        }
    }
}
