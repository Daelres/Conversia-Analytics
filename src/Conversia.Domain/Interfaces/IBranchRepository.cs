using Conversia.Domain.Entities;

namespace Conversia.Domain.Interfaces;

public interface IBranchRepository
{
    /// <summary>Untracked, read-only lookup. Tenant-scoped: another tenant's branch is indistinguishable from a missing one (null).</summary>
    Task<Branch?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The current tenant's branches ordered by name.</summary>
    Task<IReadOnlyList<Branch>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists a new branch. Throws DuplicateBranchNameException when the tenant already holds the name (ignoring case).</summary>
    Task AddAsync(Branch branch, CancellationToken cancellationToken = default);
}
