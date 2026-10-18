using Conversia.Application.Ingestion;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Webhooks;

namespace Conversia.API.HostedServices;

/// <summary>
/// Drains the webhook queue in the background: up to <c>BatchSize</c> events per cycle, then it waits <c>PollIntervalMilliseconds</c>
/// unless the cycle found a full batch. A failing cycle (database unreachable, ...) is logged, with the sanitized description and never
/// the raw exception, and the next one waits longer: 2 s, 4 s, ... up to 60 s, back to normal after the first cycle that works. The
/// failure of a single event is the runner's business (retry with back-off, then Failed). Registered only when
/// <c>Webhooks:Processing:Enabled</c> is true.
/// </summary>
internal sealed class WebhookEventProcessingService(
    WebhookEventRunner runner,
    WebhookProcessingOptions options,
    WebhookQueueBacklogReporter backlog,
    ILogger<WebhookEventProcessingService> logger) : BackgroundService
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
                    "The webhook worker cycle failed ({Failures} in a row); next try in {Delay:0.#} s. Is the database reachable and migrated? {Error}",
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
