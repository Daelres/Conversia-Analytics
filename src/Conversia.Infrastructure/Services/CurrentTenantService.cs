using Conversia.Application.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Conversia.Infrastructure.Services;

/// <summary>
/// Resolves the current tenant: the one set explicitly through <see cref="ITenantContext"/> (webhook request and background worker,
/// which have no JWT), otherwise the <c>tenant_id</c> JWT claim of the active <see cref="HttpContext"/>. Used by
/// <c>ApplicationDbContext</c>'s global query filters — docs/USERS_TENANTS_PERMISSIONS.md, "Multi-tenancy enforcement".
/// Registered once per DI scope under both interfaces, so the tenant set in a scope is the one its DbContext filters by.
/// </summary>
public class CurrentTenantService : ICurrentTenantService, ITenantContext
{
    private const string TenantIdClaimType = "tenant_id";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private Guid? _explicitTenantId;

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
            if (_explicitTenantId is { } explicitTenantId)
            {
                return explicitTenantId;
            }

            var claim = _httpContextAccessor.HttpContext?.User.FindFirst(TenantIdClaimType)?.Value;
            return Guid.TryParse(claim, out var tenantId) ? tenantId : Guid.Empty;
        }
    }

    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("The tenant id must not be empty.", nameof(tenantId));
        }

        _explicitTenantId = tenantId;
    }
}
