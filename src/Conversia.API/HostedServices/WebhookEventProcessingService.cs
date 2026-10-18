using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Webhooks;

namespace Conversia.API.HostedServices;

/// <summary>
/// Drains the webhook queue in the background: up to <c>BatchSize</c> events per cycle, then it waits <c>PollIntervalMilliseconds</c>
/// unless the cycle found a full batch. A failure of a cycle (database unreachable, ...) is logged and the next cycle tries again;
/// the failure of a single event is the runner's business (retry with back-off, then Failed). Registered only when
/// <c>Webhooks:Processing:Enabled</c> is true.
/// </summary>
internal sealed class WebhookEventProcessingService(
    WebhookEventRunner runner,
    WebhookProcessingOptions options,
    ILogger<WebhookEventProcessingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before the first query.
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                while (handled < options.BatchSize && await runner.ProcessNextAsync(stoppingToken))
                {
                    handled++;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "The webhook worker cycle failed; it will try again. Is the database reachable and migrated?");
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (handled < options.BatchSize)
            {
                try
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
