using Conversia.Domain.ReadModels;

namespace Conversia.Domain.Interfaces;

/// <summary>
/// Cross-tenant counts for the platform administrator's monitoring (docs/PLATFORM_ADMIN.md, "Monitoring"). Like
/// <see cref="IPlatformAdminQueries"/>, they look across companies on purpose and project counts, times, codes and money only: never a message,
/// a contact, a conversation, a payload or an error message.
/// </summary>
public interface IPlatformMonitoringQueries
{
    Task<PlatformActivity> GetPlatformAsync(MonitoringRequest request, CancellationToken cancellationToken = default);

    Task<TenantActivity> GetTenantAsync(Guid tenantId, MonitoringRequest request, CancellationToken cancellationToken = default);
}
