using Conversia.Application.DTOs.Tenants;

namespace Conversia.Application.Interfaces;

/// <summary>The caller's own company, as seen by its users (the platform administrator has its own service).</summary>
public interface ITenantService
{
    /// <summary>The company and its line usage. Throws <see cref="Exceptions.TenantNotFoundException"/> when the company no longer exists.</summary>
    Task<TenantInfoResponse> GetCurrentAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
