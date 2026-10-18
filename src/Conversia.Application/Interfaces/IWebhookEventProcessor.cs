using Conversia.Domain.Entities;

namespace Conversia.Application.Interfaces;

/// <param name="MessagesStored">Messages that were new and are now stored.</param>
/// <param name="MessagesAlreadyStored">Messages of the delivery that an earlier delivery had already stored.</param>
/// <param name="Ignored">Entries with nothing to store: delivery receipts, malformed entries, another number's messages, unknown shapes.</param>
public sealed record WebhookProcessingResult(int MessagesStored, int MessagesAlreadyStored, int Ignored);

/// <summary>
/// Turns one stored webhook delivery into contacts, conversations, messages and opportunities. Idempotent: processing the same
/// delivery again, or another delivery that repeats some of its messages, stores nothing twice. The caller owns the transaction and
/// has already set the tenant of the event (<see cref="ITenantContext"/>).
/// </summary>
public interface IWebhookEventProcessor
{
    Task<WebhookProcessingResult> ProcessAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default);
}
