using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;

namespace Conversia.Application.Interfaces;

public interface IPermissionService
{
    /// <summary>
    /// Returns the caller's own profile plus their currently assigned
    /// permissions. Backs <c>GET /api/users/me</c>.
    /// </summary>
    Task<UserProfileResponse> GetMyProfileAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the users belonging to the given tenant only. Backs
    /// <c>GET /api/users</c>.
    /// </summary>
    Task<IReadOnlyList<UserSummaryResponse>> ListUsersInTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns a permission to a user. Throws
    /// <see cref="Exceptions.UserNotFoundException"/> if the target user does
    /// not exist or belongs to a different tenant than <paramref name="callerTenantId"/> —
    /// this is a tenant-isolation boundary, not a convenience filter.
    /// </summary>
    Task AssignPermissionAsync(Guid callerTenantId, Guid targetUserId, Permission permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a permission from a user. Idempotent: revoking a permission the
    /// target does not hold succeeds. Throws
    /// <see cref="Exceptions.UserNotFoundException"/> under the same
    /// tenant-isolation rule as <see cref="AssignPermissionAsync"/>.
    /// </summary>
    Task RevokePermissionAsync(Guid callerTenantId, Guid targetUserId, Permission permission, CancellationToken cancellationToken = default);
}
