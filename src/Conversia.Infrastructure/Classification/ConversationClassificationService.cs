using Conversia.Application.Ingestion;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Webhooks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Classification;

/// <summary>
/// Drains the classification backlog in the background: up to <c>BatchSize</c> conversations per cycle, then it waits
/// <c>PollIntervalMilliseconds</c> unless the cycle found a full batch. A failing cycle (database unreachable, ...) is logged with the
/// sanitized description, never the raw exception, and the next one waits longer (the webhook worker's back-off: 2 s, 4 s, ... up to 60 s). The
/// failure of a single conversation is the runner's business (count, retry delay, give up). Registered only when <c>Classification:Enabled</c> is true.
/// </summary>
public sealed class ConversationClassificationService(
    ConversationClassificationRunner runner,
    ClassificationOptions options,
    ClassificationBacklogReporter backlog,
    ILogger<ConversationClassificationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the host finish starting before the first query.
            await Task.Yield();
            await RunAsync(stoppingToken);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: whatever the cycle in flight threw on its way out is not a failure of the host.
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            TimeSpan wait;
            try
            {
                while (handled < options.BatchSize && await runner.ProcessNextAsync(stoppingToken))
                {
                    handled++;
                }

                failures = 0;
                wait = handled < options.BatchSize ? options.PollInterval : TimeSpan.Zero;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                failures++;
                wait = WebhookWorkerBackoff.FailureDelay(failures);
                logger.LogError(
                    "The classification worker cycle failed ({Failures} in a row); next try in {Delay:0.#} s. Is the database reachable and migrated? {Error}",
                    failures, wait.TotalSeconds, WebhookErrors.Describe(ex));
            }

            await backlog.ReportIfDueAsync(stoppingToken);

            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, stoppingToken);
            }
        }
    }
}
