using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Users;

/// <summary>
/// Response for <c>GET /api/users/me</c> — the caller's own identity plus
/// their currently assigned permissions (resolved fresh per request, never
/// embedded in the JWT — docs/USERS_TENANTS_PERMISSIONS.md).
/// </summary>
public class UserProfileResponse
{
    public Guid UserId { get; set; }

    public Guid TenantId { get; set; }

    public bool IsOwner { get; set; }

    public IReadOnlyList<Permission> Permissions { get; set; } = Array.Empty<Permission>();
}
