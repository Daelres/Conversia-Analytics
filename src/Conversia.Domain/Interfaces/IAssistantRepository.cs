using Conversia.Domain.Entities;

namespace Conversia.Domain.Interfaces;

/// <summary>
/// The AI sales assistant's rows of the caller's company (docs/ASSISTANT.md): its settings, the per-conversation state and the knowledge
/// base versions. Tenant-filtered like every company table. Writes are tracked and saved by <see cref="SaveChangesAsync"/> (or by the
/// transaction the caller is in: the conversation lock of ingestion or of the send path).
/// </summary>
public interface IAssistantRepository
{
    /// <summary>The company's settings, tracked; null when it never saved any (the defaults apply).</summary>
    Task<AssistantSettings?> GetSettingsAsync(CancellationToken cancellationToken = default);

    void AddSettings(AssistantSettings settings);

    /// <summary>The conversation's state, tracked and locked (<c>FOR UPDATE</c>) for the caller's transaction; null when it has none.</summary>
    Task<ConversationAssistant?> FindStateForUpdateAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>The conversation's state, untracked.</summary>
    Task<ConversationAssistant?> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default);

    void AddState(ConversationAssistant state);

    /// <summary>The latest versions, newest first, without their content.</summary>
    Task<IReadOnlyList<AssistantKnowledgeVersion>> ListKnowledgeAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>One version by number, tracked.</summary>
    Task<AssistantKnowledgeVersion?> GetKnowledgeAsync(int number, CancellationToken cancellationToken = default);

    /// <summary>The published version, tracked; null when none is.</summary>
    Task<AssistantKnowledgeVersion?> GetPublishedKnowledgeAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a new version with the next number of the company, serialized per company (a unique index decides a race).</summary>
    Task<AssistantKnowledgeVersion> AddKnowledgeAsync(Guid tenantId, string content, Guid? savedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes version <paramref name="number"/> and archives the one published before, in one transaction. Null when the version does not
    /// exist; throws <see cref="Exceptions.ConcurrencyConflictException"/> when another publication won the race.
    /// </summary>
    Task<AssistantKnowledgeVersion?> PublishKnowledgeAsync(int number, Guid? userId, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>Adds one to a counter of the company's assistant activity for <paramref name="day"/> (local), in the caller's transaction if any.</summary>
    Task CountAsync(Guid tenantId, bool isSandbox, string metric, DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>Queues an append-only history row; written by the next save.</summary>
    void Audit(AssistantAuditEntry entry);

    /// <summary>The latest history rows, newest first.</summary>
    Task<IReadOnlyList<AssistantAuditEntry>> ListAuditAsync(int take, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
