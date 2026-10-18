namespace Conversia.Application.Interfaces;

/// <summary>
/// Resolves the tenant id of the currently authenticated request from the
/// tenant_id JWT claim. Used by <c>ApplicationDbContext</c> to scope the
/// global query filter on Users/UserPermissions — see
/// docs/USERS_TENANTS_PERMISSIONS.md, "Multi-tenancy enforcement".
/// </summary>
public interface ICurrentTenantService
{
    Guid TenantId { get; }
}
