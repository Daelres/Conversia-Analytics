using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Domain.Interfaces;

public interface IUserPermissionRepository
{
    Task<bool> HasPermissionAsync(Guid userId, Permission permission, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Permission>> ListByUserAsync(Guid userId, CancellationToken cancellationToken = default);

    Task AssignAsync(UserPermission userPermission, CancellationToken cancellationToken = default);

    /// <summary>Removes the permission from the user; a no-op when the user does not hold it.</summary>
    Task RemoveAsync(Guid userId, Permission permission, CancellationToken cancellationToken = default);
}
