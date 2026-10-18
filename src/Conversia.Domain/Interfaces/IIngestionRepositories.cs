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
    /// <summary>
    /// Tracked lookup of the open conversation of a contact on a channel (the database allows at most one). The row is locked
    /// (<c>FOR UPDATE</c>) until the caller's transaction ends, so the close-by-inactivity job cannot close a conversation a message is
    /// being added to: either it closed it first, and this finds no open conversation, or it skips the row.
    /// </summary>
    Task<Conversation?> FindOpenAsync(Guid contactId, Guid channelId, CancellationToken cancellationToken = default);

    /// <summary>Persists a new conversation together with its opportunity.</summary>
    Task AddAsync(Conversation conversation, Opportunity opportunity, CancellationToken cancellationToken = default);

    /// <summary>Persists the changes made to a conversation loaded through <see cref="FindOpenAsync"/> or <see cref="GetForUpdateAsync"/>.</summary>
    Task UpdateAsync(Conversation conversation, CancellationToken cancellationToken = default);

    /// <summary>Tracked lookup by id for the load-modify-<see cref="UpdateAsync"/> path. Another tenant's conversation is indistinguishable from a missing one (null).</summary>
    Task<Conversation?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="change"/> on a conversation and its opportunity and saves both, holding the lock of the conversation row for the
    /// whole read-change-save, so it cannot interleave with the classifier or the close-by-inactivity job (which take the same lock before
    /// they touch the opportunity). Returns the saved opportunity, or null when the conversation does not exist (or belongs to another
    /// tenant). If <paramref name="change"/> throws, nothing is saved.
    /// </summary>
    Task<Opportunity?> ChangeOpportunityAsync(Guid conversationId, Action<Conversation, Opportunity> change, CancellationToken cancellationToken = default);
}

public interface IMessageRepository
{
    Task<bool> ExistsAsync(Guid channelId, string providerMessageId, CancellationToken cancellationToken = default);

    Task AddAsync(Message message, CancellationToken cancellationToken = default);

    /// <summary>Direction and time of every message of a conversation, for <see cref="ConversationTimeline"/>.</summary>
    Task<IReadOnlyList<(MessageDirection Direction, DateTime SentAt)>> ListTimesAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
