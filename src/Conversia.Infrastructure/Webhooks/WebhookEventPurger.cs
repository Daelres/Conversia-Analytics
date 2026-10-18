using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Webhooks;

/// <summary>Deletes old webhook events, across all tenants (retention is an operations rule), in batches of 1 000.</summary>
public sealed class WebhookEventPurger(ApplicationDbContext db)
{
    public const int BatchSize = 1000;

    /// <summary>Processed events whose <c>ProcessedAt</c> is before <paramref name="processedBefore"/>, and Failed events last touched before <paramref name="failedBefore"/>.</summary>
    public async Task<(int Processed, int Failed)> PurgeAsync(DateTime processedBefore, DateTime failedBefore, CancellationToken cancellationToken = default)
    {
        processedBefore = DateTime.SpecifyKind(processedBefore, DateTimeKind.Utc);
        failedBefore = DateTime.SpecifyKind(failedBefore, DateTimeKind.Utc);

        var processed = await DeleteInBatchesAsync(
            e => e.Status == WebhookEventStatus.Processed && (e.ProcessedAt ?? e.ReceivedAt) < processedBefore, cancellationToken);
        var failed = await DeleteInBatchesAsync(
            e => e.Status == WebhookEventStatus.Failed && (e.UpdatedAt ?? e.ReceivedAt) < failedBefore, cancellationToken);
        return (processed, failed);
    }

    private async Task<int> DeleteInBatchesAsync(System.Linq.Expressions.Expression<Func<Domain.Entities.WebhookEvent, bool>> predicate, CancellationToken cancellationToken)
    {
        var total = 0;
        int deleted;
        do
        {
            var batch = db.WebhookEvents.IgnoreQueryFilters().Where(predicate).OrderBy(e => e.ReceivedAt).Take(BatchSize).Select(e => e.Id);
            deleted = await db.WebhookEvents.IgnoreQueryFilters().Where(e => batch.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        return total;
    }
}
