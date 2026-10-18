using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;

namespace Conversia.Web.Services;

public static class UserProfileExtensions
{
    /// <summary>What the page may offer: an owner holds everything in the tenant. The API stays the authority.</summary>
    public static bool Can(this UserProfileResponse profile, Permission permission)
        => profile.IsOwner || profile.Permissions.Contains(permission);
}
