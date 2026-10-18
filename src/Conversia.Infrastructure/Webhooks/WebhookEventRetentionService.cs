using Conversia.Application.Ingestion;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Webhooks;

/// <summary>
/// Deletes webhook events past their retention (<c>Webhooks:Retention</c>): Processed ones after <c>RetentionDays</c> (14), Failed ones, which an
/// operator may still want to look at or requeue, after <c>FailedRetentionDays</c> (60). Once at startup and then every 24 hours, as a
/// <see cref="BackgroundService"/> so the startup purge never delays readiness; a failure is logged (sanitized) and the next run tries again.
/// The raw payloads hold customers' phone numbers and message text: this is the purge the data-protection policy (Ley 1581 de 2012) needs.
/// </summary>
public sealed class WebhookEventRetentionService(
    IServiceScopeFactory scopeFactory,
    WebhookRetentionOptions options,
    TimeProvider timeProvider,
    ILogger<WebhookEventRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var timer = new PeriodicTimer(Interval, timeProvider);
        try
        {
            do
            {
                await PurgeOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    private async Task PurgeOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            await using var scope = scopeFactory.CreateAsyncScope();
            var (processed, failed) = await scope.ServiceProvider.GetRequiredService<WebhookEventPurger>()
                .PurgeAsync(now.AddDays(-options.RetentionDays), now.AddDays(-options.FailedRetentionDays), cancellationToken);
            if (processed + failed > 0)
            {
                logger.LogInformation(
                    "Webhook event retention purged {Processed} processed events older than {Days} days and {Failed} failed ones older than {FailedDays} days.",
                    processed, options.RetentionDays, failed, options.FailedRetentionDays);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("The webhook event retention purge failed; it will be tried again at the next run: {Error}", WebhookErrors.Describe(ex));
        }
    }
}
