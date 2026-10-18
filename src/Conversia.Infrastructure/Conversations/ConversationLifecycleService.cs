using Conversia.Application.Ingestion;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Conversations;

/// <summary>
/// Closes the idle conversations (<see cref="ConversationAutoCloser"/>) once at startup and then every <c>Conversations:AutoCloseIntervalMinutes</c>,
/// as a <see cref="BackgroundService"/> so the first run never delays readiness. A run closes batch after batch until none is left due; a failure
/// is logged (sanitized, never the raw exception) and the next run tries again. Registered only when <c>Conversations:AutoCloseEnabled</c> is true.
/// </summary>
public sealed class ConversationLifecycleService(
    IServiceScopeFactory scopeFactory,
    ConversationLifecycleOptions options,
    TimeProvider timeProvider,
    ILogger<ConversationLifecycleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var timer = new PeriodicTimer(options.Interval, timeProvider);
        try
        {
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    /// <summary>Closes every idle conversation that is due now. Never throws.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var closed = 0;
            var lost = 0;
            ConversationAutoCloser.ClosedBatch batch;
            do
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                batch = await scope.ServiceProvider.GetRequiredService<ConversationAutoCloser>()
                    .CloseBatchAsync(timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
                closed += batch.Closed;
                lost += batch.LostForNoResponse;
            }
            while (batch.Closed == options.BatchSize);

            if (closed > 0)
            {
                logger.LogInformation(
                    "Closed {Closed} conversations idle for more than {Days} days; {Lost} opportunities were lost for lack of an answer.",
                    closed, options.AfterDays, lost);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Closing the idle conversations failed; it will be tried again at the next run: {Error}", WebhookErrors.Describe(ex));
        }
    }
}
