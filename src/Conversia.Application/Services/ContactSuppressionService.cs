using Conversia.Application.DTOs.Contacts;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.Services;

/// <summary>
/// Suppresses a contact (see <see cref="IContactSuppressionService"/>). There is no tenant-side audit table: the trail is the log line
/// below (event 4201 <c>ContactSuppressed</c>), which holds ids and counts only, never the number, the name or a message.
/// </summary>
public sealed class ContactSuppressionService(IContactSuppressionStore store, TimeProvider time, ILogger<ContactSuppressionService> logger) : IContactSuppressionService
{
    public static readonly EventId ContactSuppressed = new(4201, nameof(ContactSuppressed));

    public async Task<ContactSuppressionResponse> SuppressAsync(Guid tenantId, Guid userId, Guid contactId, CancellationToken cancellationToken = default)
    {
        var counts = await store.SuppressAsync(tenantId, contactId, userId, time.GetUtcNow().UtcDateTime, cancellationToken)
            ?? throw new ContactNotFoundException();

        logger.LogInformation(
            ContactSuppressed,
            "Contact {ContactId} of tenant {TenantId} was suppressed by user {UserId}: {Conversations} conversations, {Messages} messages, {Opportunities} opportunities, {WebhookEvents} processed deliveries and {AiUsageRows} AI usage rows deleted.",
            contactId, tenantId, userId, counts.Conversations, counts.Messages, counts.Opportunities, counts.WebhookEvents, counts.AiUsageRows);

        return new ContactSuppressionResponse
        {
            ContactId = contactId,
            Conversations = counts.Conversations,
            Messages = counts.Messages,
            Opportunities = counts.Opportunities,
            WebhookEvents = counts.WebhookEvents,
        };
    }
}
