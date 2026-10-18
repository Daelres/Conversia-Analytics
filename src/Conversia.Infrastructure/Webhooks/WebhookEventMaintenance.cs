using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Webhooks;

public sealed class WebhookEventMaintenance(ApplicationDbContext dbContext, ITenantContext tenantContext, TimeProvider timeProvider) : IWebhookEventMaintenance
{
    public async Task<int> RequeueFailedAsync(Guid tenantId, Guid? eventId = null, CancellationToken cancellationToken = default)
    {
        // The tenant filter does the isolation: this scope only sees the events of the tenant it was given.
        tenantContext.SetTenant(tenantId);
        var failed = await dbContext.WebhookEvents
            .Where(e => e.Status == WebhookEventStatus.Failed && (eventId == null || e.Id == eventId))
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var webhookEvent in failed)
        {
            webhookEvent.Requeue(now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return failed.Count;
    }
}
