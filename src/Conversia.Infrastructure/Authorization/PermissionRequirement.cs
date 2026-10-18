using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace Conversia.Infrastructure.Authorization;

public class PermissionRequirement : IAuthorizationRequirement
{
    public Permission Permission { get; }

    public PermissionRequirement(Permission permission)
    {
        Permission = permission;
    }
}
