using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Domain.Interfaces;

/// <summary>Persistence of the webhook queue. Tenant-scoped like every repository: the caller sets the tenant first.</summary>
public interface IWebhookEventRepository
{
    /// <summary>
    /// Stores the event unless the channel already holds one with the same body hash. Returns false for that duplicate (nothing
    /// is written). Throws InvalidWebhookPayloadException when PostgreSQL refuses the payload as <c>jsonb</c>.
    /// </summary>
    Task<bool> AddIfNewAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default);
}

public interface IContactRepository
{
    /// <summary>Tracked lookup by normalized WhatsApp id, for the load-modify-<see cref="UpdateAsync"/> path.</summary>
    Task<Contact?> GetByWaIdAsync(string waId, CancellationToken cancellationToken = default);

    Task AddAsync(Contact contact, CancellationToken cancellationToken = default);

    /// <summary>Persists the changes made to a contact loaded through <see cref="GetByWaIdAsync"/>.</summary>
    Task UpdateAsync(Contact contact, CancellationToken cancellationToken = default);
}

public interface IConversationRepository
{
    /// <summary>Tracked lookup of the open conversation of a contact on a channel (the database allows at most one).</summary>
    Task<Conversation?> FindOpenAsync(Guid contactId, Guid channelId, CancellationToken cancellationToken = default);

    /// <summary>Persists a new conversation together with its opportunity.</summary>
    Task AddAsync(Conversation conversation, Opportunity opportunity, CancellationToken cancellationToken = default);

    /// <summary>Persists the changes made to a conversation loaded through <see cref="FindOpenAsync"/>.</summary>
    Task UpdateAsync(Conversation conversation, CancellationToken cancellationToken = default);
}

public interface IMessageRepository
{
    Task<bool> ExistsAsync(Guid channelId, string providerMessageId, CancellationToken cancellationToken = default);

    Task AddAsync(Message message, CancellationToken cancellationToken = default);

    /// <summary>Direction and time of every message of a conversation, for <see cref="ConversationTimeline"/>.</summary>
    Task<IReadOnlyList<(MessageDirection Direction, DateTime SentAt)>> ListTimesAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
