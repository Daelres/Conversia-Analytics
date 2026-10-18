using Conversia.Domain.Enums;

namespace Conversia.Web.Services;

/// <summary>
/// What a person reads for a permission. The enum names stay identifiers (routes, JSON, form values and <c>data-*</c> hooks);
/// a permission added to the catalog needs its label here, or it is shown under its name.
/// </summary>
public static class PermissionLabels
{
    private static readonly IReadOnlyDictionary<Permission, string> Labels = new Dictionary<Permission, string>
    {
        [Permission.UsersView] = "Ver usuarios",
        [Permission.UsersManage] = "Gestionar usuarios",
        [Permission.ProductsView] = "Ver productos",
        [Permission.ProductsManage] = "Gestionar productos",
        [Permission.OrganizationView] = "Ver organización",
        [Permission.OrganizationManage] = "Gestionar organización",
    };

    public static string For(Permission permission)
        => Labels.TryGetValue(permission, out var label) ? label : permission.ToString();
}
