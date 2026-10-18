using Conversia.Application.Ingestion;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Webhooks;

/// <summary>Logs the size of the webhook backlog at Information, at most once per <see cref="Interval"/> however often it is asked.</summary>
public sealed class WebhookQueueBacklogReporter(IWebhookQueueMonitor monitor, TimeProvider timeProvider, ILogger<WebhookQueueBacklogReporter> logger)
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
            var snapshot = await monitor.GetSnapshotAsync(cancellationToken);
            logger.LogInformation(
                "Webhook queue: {Pending} pending, oldest waiting {OldestSeconds:0} s, {Failed} failed in the last hour.",
                snapshot.PendingCount, snapshot.OldestPendingAge?.TotalSeconds ?? 0, snapshot.FailedLastHour);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The webhook backlog could not be read: {Error}", WebhookErrors.Describe(ex));
        }
    }
}
