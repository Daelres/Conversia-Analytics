using Conversia.Application.DTOs.Contacts;

namespace Conversia.Application.Interfaces;

/// <summary>
/// The suppression of one contact on request of the customer (Ley 1581, habeas data; docs/USERS_TENANTS_PERMISSIONS.md, "Contact
/// suppression"). Permission <c>ContactsSuppress</c>.
/// </summary>
public interface IContactSuppressionService
{
    /// <summary>
    /// Deletes the contact, its conversations, messages and opportunities, its AI usage rows per conversation and the processed raw deliveries
    /// that carried its number, and leaves the mark that stops its old messages from recreating it. Irreversible. Throws
    /// <see cref="Exceptions.ContactNotFoundException"/>.
    /// </summary>
    Task<ContactSuppressionResponse> SuppressAsync(Guid tenantId, Guid userId, Guid contactId, CancellationToken cancellationToken = default);
}

/// <summary>The persistence of a contact suppression: everything in one transaction, serialized with the ingestion of the company.</summary>
public interface IContactSuppressionStore
{
    /// <summary>Null when the contact does not exist in the company.</summary>
    Task<ContactSuppressionCounts?> SuppressAsync(Guid tenantId, Guid contactId, Guid userId, DateTime now, CancellationToken cancellationToken = default);
}

public sealed record ContactSuppressionCounts(int Conversations, int Messages, int Opportunities, int WebhookEvents, int AiUsageRows);
