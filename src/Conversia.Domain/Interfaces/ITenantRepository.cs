using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Domain.Interfaces;

public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The status of the company and, when suspended, why; null when it does not exist. Untracked projection of two columns: run on every
    /// authenticated request.
    /// </summary>
    Task<TenantLifecycleState?> GetLifecycleAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Whether the company is in sandbox mode (docs/SANDBOX.md), or null when it does not exist. Untracked projection of one column.</summary>
    Task<bool?> GetSandboxModeAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(Tenant tenant, CancellationToken cancellationToken = default);
}

/// <summary>Where a company stands: its status and, when suspended, the cause.</summary>
public sealed record TenantLifecycleState(TenantStatus Status, SuspensionCause? Cause);
