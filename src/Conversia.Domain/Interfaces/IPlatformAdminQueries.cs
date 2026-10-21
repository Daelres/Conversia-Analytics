using Conversia.Domain.ReadModels;

namespace Conversia.Domain.Interfaces;

/// <summary>
/// Cross-tenant reads for the platform administrator. They look across companies on purpose (the only queries besides the webhook
/// lookup and the workers that do), and return business facts only: never conversations, messages, contacts or users.
/// </summary>
public interface IPlatformAdminQueries
{
    Task<ReadPage<TenantAdminSummary>> ListTenantsAsync(TenantAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Null for an unknown id.</summary>
    Task<TenantAdminDetail?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<ReadPage<PlatformAuditEntry>> ListAuditAsync(Guid? tenantId, int page, int pageSize, CancellationToken cancellationToken = default);
}
