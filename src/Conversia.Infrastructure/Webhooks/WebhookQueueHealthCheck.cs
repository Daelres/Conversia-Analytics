using Conversia.Application.Ingestion;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Webhooks;

/// <summary>
/// Healthy while the webhook queue is flowing; Degraded when the oldest pending event is older than <c>MaxPendingAgeMinutes</c> or more than
/// <c>MaxFailedPerHour</c> events failed in the last hour; Unhealthy when the database cannot be asked. It reports a status and nothing else.
/// </summary>
public sealed class WebhookQueueHealthCheck(IWebhookQueueMonitor monitor, WebhookHealthOptions options, ILogger<WebhookQueueHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        WebhookQueueSnapshot snapshot;
        try
        {
            snapshot = await monitor.GetSnapshotAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("The webhook queue could not be read for the health check: {Error}", WebhookErrors.Describe(ex));
            return HealthCheckResult.Unhealthy("The webhook queue cannot be read.");
        }

        if (snapshot.OldestPendingAge is { } age && age > TimeSpan.FromMinutes(options.MaxPendingAgeMinutes))
        {
            return HealthCheckResult.Degraded("The oldest pending webhook event is too old.");
        }

        return snapshot.FailedLastHour > options.MaxFailedPerHour
            ? HealthCheckResult.Degraded("Too many webhook events failed in the last hour.")
            : HealthCheckResult.Healthy();
    }
}
