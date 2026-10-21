using Conversia.Domain.Entities;

namespace Conversia.Domain.Interfaces;

/// <summary>
/// Starts a change that depends on the lines a company has: it locks the company's row (<c>SELECT ... FOR UPDATE</c>) inside a
/// transaction, so two changes to the same company (a line switched on, a quota lowered, a status changed) run one after the other
/// and the quota can never be exceeded by a race.
/// </summary>
public interface ITenantUnitOfWork
{
    /// <summary>Null when the company does not exist. Dispose the result without committing to roll back.</summary>
    Task<ITenantChange?> BeginAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>A locked company and what a change to it may touch. Nothing is persisted until <see cref="CommitAsync"/>.</summary>
public interface ITenantChange : IAsyncDisposable
{
    /// <summary>The company, tracked and locked.</summary>
    Tenant Tenant { get; }

    Task<int> CountActiveLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>A tracked channel of THIS company (any tenant filter aside); null when it does not exist or belongs to another company.</summary>
    Task<WhatsAppChannel?> GetChannelAsync(Guid channelId, CancellationToken cancellationToken = default);

    /// <summary>Queues an audit row; it is written, with the rest, by <see cref="CommitAsync"/>.</summary>
    void Record(PlatformAuditLog entry);

    Task CommitAsync(CancellationToken cancellationToken = default);
}
