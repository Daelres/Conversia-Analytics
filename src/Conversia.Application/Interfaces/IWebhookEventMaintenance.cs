namespace Conversia.Application.Interfaces;

/// <summary>
/// Operator actions on the webhook queue. There is no endpoint for them on purpose: they are for the operator (a console, a script or a
/// future owner-only screen), which must itself be authorized; the same effect by SQL is documented in docs/WEBHOOKS.md.
/// </summary>
public interface IWebhookEventMaintenance
{
    /// <summary>
    /// Puts the tenant's <c>Failed</c> events back in the queue with a fresh budget of attempts: all of them, or only
    /// <paramref name="eventId"/> when given. Returns how many were requeued. Another tenant's events are never touched.
    /// </summary>
    Task<int> RequeueFailedAsync(Guid tenantId, Guid? eventId = null, CancellationToken cancellationToken = default);
}
