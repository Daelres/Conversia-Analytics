using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Infrastructure.Webhooks;

/// <param name="PendingCount">Events waiting (including those waiting for a retry).</param>
/// <param name="OldestPendingAge">How long the oldest pending event has been waiting since it was received; null when none.</param>
/// <param name="FailedLastHour">Events that became Failed in the last hour.</param>
public sealed record WebhookQueueSnapshot(int PendingCount, TimeSpan? OldestPendingAge, int FailedLastHour);

public interface IWebhookQueueMonitor
{
    Task<WebhookQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>A few cheap counts over the whole queue (all tenants: it is an operations view, and returns no customer data).</summary>
public sealed class WebhookQueueMonitor(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : IWebhookQueueMonitor
{
    public async Task<WebhookQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var lastHour = now.AddHours(-1);

        // Waiting work that a worker will take: the events of a pending or suspended company wait on purpose and are not a backlog.
        var pending = db.WebhookEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Status == WebhookEventStatus.Pending && db.Tenants.Any(t => t.Id == e.TenantId && t.Status == TenantStatus.Active));
        var count = await pending.CountAsync(cancellationToken);
        var oldest = count == 0 ? null : await pending.MinAsync(e => (DateTime?)e.ReceivedAt, cancellationToken);
        var failed = await db.WebhookEvents.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(e => e.Status == WebhookEventStatus.Failed && (e.UpdatedAt ?? e.ReceivedAt) >= lastHour, cancellationToken);

        return new WebhookQueueSnapshot(count, oldest is null ? null : now - oldest.Value, failed);
    }
}
