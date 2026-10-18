using Conversia.Domain.Entities;

namespace Conversia.Domain.Interfaces;

public interface IWhatsAppChannelRepository
{
    /// <summary>Untracked, read-only lookup. Tenant-scoped: another tenant's channel is indistinguishable from a missing one (null).</summary>
    Task<WhatsAppChannel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The current tenant's channels ordered by display number.</summary>
    Task<IReadOnlyList<WhatsAppChannel>> ListAsync(CancellationToken cancellationToken = default);

    Task AddAsync(WhatsAppChannel channel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Untracked lookup by the public key of the webhook URL, <b>across all tenants</b>: the anonymous webhook request has no tenant
    /// until the channel (and its secret) identifies it. The only repository method that ignores the tenant filter on purpose.
    /// </summary>
    Task<WhatsAppChannel?> GetByChannelKeyAsync(string channelKey, CancellationToken cancellationToken = default);
}
