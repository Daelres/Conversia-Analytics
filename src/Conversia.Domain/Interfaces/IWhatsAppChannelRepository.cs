using Conversia.Domain.Entities;

namespace Conversia.Domain.Interfaces;

public interface IWhatsAppChannelRepository
{
    /// <summary>Untracked, read-only lookup. Tenant-scoped: another tenant's channel is indistinguishable from a missing one (null).</summary>
    Task<WhatsAppChannel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The current tenant's channels ordered by display number.</summary>
    Task<IReadOnlyList<WhatsAppChannel>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>How many channels the current tenant has registered and how many of them are switched on.</summary>
    Task<(int Registered, int Active)> CountAsync(CancellationToken cancellationToken = default);

    Task AddAsync(WhatsAppChannel channel, CancellationToken cancellationToken = default);

    /// <summary>Tracked lookup for the load-modify-<see cref="UpdateAsync"/> path; same tenant scoping as <see cref="GetByIdAsync"/>.</summary>
    Task<WhatsAppChannel?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Persists the changes made to a channel loaded through <see cref="GetForUpdateAsync"/>.</summary>
    Task UpdateAsync(WhatsAppChannel channel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Untracked lookup by the public key of the webhook URL, <b>across all tenants</b>: the anonymous webhook request has no tenant
    /// until the channel (and its secret) identifies it. The only repository method that ignores the tenant filter on purpose.
    /// </summary>
    Task<WhatsAppChannel?> GetByChannelKeyAsync(string channelKey, CancellationToken cancellationToken = default);
}
