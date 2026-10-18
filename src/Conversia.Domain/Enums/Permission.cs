namespace Conversia.Domain.Enums;

/// <summary>
/// The permission catalog — see docs/USERS_TENANTS_PERMISSIONS.md. This enum
/// *is* the catalog (not a lookup table); adding a new permission is adding
/// an enum value, not a migration.
/// </summary>
public enum Permission
{
    UsersView = 1,
    UsersManage = 2,
    ProductsView = 3,
    ProductsManage = 4,
    OrganizationView = 5,
    OrganizationManage = 6,
    ConversationsView = 7,
    ConversationsManage = 8,
}
