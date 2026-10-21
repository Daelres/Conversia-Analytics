using Conversia.Domain.Entities;

namespace Conversia.Domain.Interfaces;

/// <summary>The platform administrators. Global: not tenant-scoped.</summary>
public interface IPlatformAdminRepository
{
    /// <summary>Untracked lookup by email (case-insensitive).</summary>
    Task<PlatformAdmin?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    Task<PlatformAdmin?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>True when the administrator exists and is active. A cheap primary-key lookup, run on every admin request.</summary>
    Task<bool> IsActiveAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Stores the administrator. Returns false (and stores nothing) when the email already exists, also under a concurrent insert.</summary>
    Task<bool> TryAddAsync(PlatformAdmin admin, CancellationToken cancellationToken = default);
}
