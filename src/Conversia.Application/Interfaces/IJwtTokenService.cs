namespace Conversia.Application.Interfaces;

public interface IJwtTokenService
{
    /// <summary>
    /// Issues a JWT carrying the "sub" (user id), "tenant_id" and "is_owner"
    /// claims described in docs/USERS_TENANTS_PERMISSIONS.md.
    /// </summary>
    string GenerateToken(Guid userId, Guid tenantId, bool isOwner);
}
