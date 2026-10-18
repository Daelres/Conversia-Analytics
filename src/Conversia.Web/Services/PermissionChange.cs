using Conversia.Domain.Enums;

namespace Conversia.Web.Services;

/// <summary>A grant/revoke request as posted by the browser; anything forged or malformed is refused before the API is called.</summary>
public readonly record struct PermissionChange(Guid UserId, Permission Permission)
{
    public static bool TryParse(string? userId, string? permission, out PermissionChange change)
    {
        change = default;
        if (!Guid.TryParse(userId, out var id) || id == Guid.Empty)
        {
            return false;
        }

        // Names only: Enum.TryParse would also accept numbers and comma lists that are not in the catalog.
        foreach (var candidate in Enum.GetValues<Permission>())
        {
            if (string.Equals(candidate.ToString(), permission, StringComparison.OrdinalIgnoreCase))
            {
                change = new PermissionChange(id, candidate);
                return true;
            }
        }

        return false;
    }
}
