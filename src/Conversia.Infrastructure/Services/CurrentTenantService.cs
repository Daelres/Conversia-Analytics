using Conversia.Application.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Conversia.Infrastructure.Services;

/// <summary>
/// Resolves the current tenant from the <c>tenant_id</c> JWT claim of the
/// active <see cref="HttpContext"/>. Used by <c>ApplicationDbContext</c>'s
/// global query filters on Users/UserPermissions —
/// docs/USERS_TENANTS_PERMISSIONS.md, "Multi-tenancy enforcement".
/// </summary>
public class CurrentTenantService : ICurrentTenantService
{
    private const string TenantIdClaimType = "tenant_id";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentTenantService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    // No authenticated principal (e.g. a background operation, or a scope
    // created outside an HTTP request) must not crash query filter
    // evaluation — Guid.Empty never matches a real tenant, so it degrades
    // to "no rows visible" rather than throwing.
    public Guid TenantId
    {
        get
        {
            var claim = _httpContextAccessor.HttpContext?.User.FindFirst(TenantIdClaimType)?.Value;
            return Guid.TryParse(claim, out var tenantId) ? tenantId : Guid.Empty;
        }
    }
}
