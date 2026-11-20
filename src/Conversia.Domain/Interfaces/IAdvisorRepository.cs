using Conversia.Domain.Entities;

namespace Conversia.Domain.Interfaces;

public interface IAdvisorRepository
{
    /// <summary>Untracked, read-only lookup. Tenant-scoped: another tenant's advisor is indistinguishable from a missing one (null).</summary>
    Task<Advisor?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The current tenant's advisors ordered by name.</summary>
    Task<IReadOnlyList<Advisor>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The advisor linked to a user of the current tenant (an active one first), or null. Untracked.</summary>
    Task<Advisor?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task AddAsync(Advisor advisor, CancellationToken cancellationToken = default);
}
