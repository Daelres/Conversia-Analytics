using Conversia.Domain.Entities;

namespace Conversia.Application.Interfaces;

/// <param name="MessagesStored">Messages that were new and are now stored.</param>
/// <param name="MessagesAlreadyStored">Messages of the delivery that an earlier delivery had already stored.</param>
/// <param name="Skipped">Messages that could not be stored (no usable phone number, id or timestamp, a group, another business number).</param>
/// <param name="Ignored">Reactions and system messages, which say nothing about the conversation.</param>
/// <param name="Statuses">Delivery receipts, which carry nothing to store.</param>
/// <param name="Suppressed">Messages of suppressed contacts that were dropped (they are also in <paramref name="Skipped"/>): the raw delivery
/// carries a suppressed customer's messages, so the caller deletes it once processed.</param>
/// <param name="TemplateStatuses">Template status notifications that changed a stored template (docs/WEBHOOKS.md, "Template status").</param>
public sealed record WebhookProcessingResult(int MessagesStored, int MessagesAlreadyStored, int Skipped, int Ignored, int Statuses, int Suppressed = 0, int TemplateStatuses = 0, int HistoryIgnored = 0, int AccountUpdates = 0);

/// <summary>
/// Turns one stored webhook delivery into contacts, conversations, messages and opportunities. Idempotent: processing the same
/// delivery again, or another delivery that repeats some of its messages, stores nothing twice. The caller owns the transaction and
/// has already set the tenant of the event (<see cref="ITenantContext"/>).
/// </summary>
public interface IWebhookEventProcessor
{
    Task<WebhookProcessingResult> ProcessAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default);
}
