using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

public class UserPermission : BaseEntity
{
    public Guid UserId { get; private set; }

    public Permission Permission { get; private set; }

    // Required by EF Core for materialization.
    private UserPermission()
    {
    }

    public static UserPermission Create(Guid userId, Permission permission)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException("El identificador del usuario es obligatorio.");
        }

        if (!Enum.IsDefined(permission))
        {
            throw new DomainException("El permiso no es un valor reconocido.");
        }

        return new UserPermission
        {
            UserId = userId,
            Permission = permission
        };
    }
}
