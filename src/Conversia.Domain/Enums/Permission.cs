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
    // 3 and 4 belonged to the retired Products permissions. Granted numbers are persisted in UserPermissions.Permission:
    // never renumber the values below and never reuse 3 or 4.
    OrganizationView = 5,
    OrganizationManage = 6,
    ConversationsView = 7,
    ConversationsManage = 8,
    MetricsView = 9,
}
