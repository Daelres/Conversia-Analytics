using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Domain.Interfaces;

public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The status of the company, or null when it does not exist. Untracked projection of one column: run on every authenticated request.</summary>
    Task<TenantStatus?> GetStatusAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(Tenant tenant, CancellationToken cancellationToken = default);
}
