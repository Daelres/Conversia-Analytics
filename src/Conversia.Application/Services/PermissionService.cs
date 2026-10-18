using Conversia.Application.DTOs.Users;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

public class PermissionService : IPermissionService
{
    private readonly IUserRepository _userRepository;
    private readonly IUserPermissionRepository _userPermissionRepository;

    public PermissionService(IUserRepository userRepository, IUserPermissionRepository userPermissionRepository)
    {
        _userRepository = userRepository;
        _userPermissionRepository = userPermissionRepository;
    }

    public async Task<UserProfileResponse> GetMyProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, cancellationToken)
            ?? throw new UserNotFoundException();

        var permissions = await _userPermissionRepository.ListByUserAsync(userId, cancellationToken);

        return new UserProfileResponse
        {
            UserId = user.Id,
            TenantId = user.TenantId,
            IsOwner = user.IsOwner,
            Permissions = permissions
        };
    }

    public async Task<IReadOnlyList<UserSummaryResponse>> ListUsersInTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var users = await _userRepository.ListByTenantAsync(tenantId, cancellationToken);

        return users
            .Select(UserSummaryResponse.From)
            .ToList();
    }

    public async Task AssignPermissionAsync(Guid callerTenantId, Guid targetUserId, Permission permission, CancellationToken cancellationToken = default)
    {
        await EnsureTargetInTenantAsync(callerTenantId, targetUserId, cancellationToken);

        var userPermission = UserPermission.Create(targetUserId, permission);
        await _userPermissionRepository.AssignAsync(userPermission, cancellationToken);
    }

    public async Task RevokePermissionAsync(Guid callerTenantId, Guid targetUserId, Permission permission, CancellationToken cancellationToken = default)
    {
        await EnsureTargetInTenantAsync(callerTenantId, targetUserId, cancellationToken);
        await _userPermissionRepository.RemoveAsync(targetUserId, permission, cancellationToken);
    }

    // A target that does not exist and a target that belongs to another
    // tenant must be indistinguishable to the caller — both throw the
    // same UserNotFoundException, mapped to 404, never 403 — see
    // docs/USERS_TENANTS_PERMISSIONS.md's anti-enumeration contract.
    private async Task EnsureTargetInTenantAsync(Guid callerTenantId, Guid targetUserId, CancellationToken cancellationToken)
    {
        var targetUser = await _userRepository.GetByIdAsync(targetUserId, cancellationToken);

        if (targetUser is null || targetUser.TenantId != callerTenantId)
        {
            throw new UserNotFoundException();
        }
    }
}
