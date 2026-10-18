using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>
/// The catalog is persisted as integers (<c>UserPermissions.Permission</c>), so the numbers are part of the
/// storage contract: renumbering or reusing a value would silently change what existing rows grant.
/// </summary>
public class PermissionCatalogTests
{
    [Theory]
    [InlineData(Permission.UsersView, 1)]
    [InlineData(Permission.UsersManage, 2)]
    [InlineData(Permission.ProductsView, 3)]
    [InlineData(Permission.ProductsManage, 4)]
    [InlineData(Permission.OrganizationView, 5)]
    [InlineData(Permission.OrganizationManage, 6)]
    [InlineData(Permission.ConversationsView, 7)]
    [InlineData(Permission.ConversationsManage, 8)]
    [InlineData(Permission.MetricsView, 9)]
    public void CatalogValues_KeepTheirPersistedNumbers(Permission permission, int persistedValue)
    {
        Assert.Equal(persistedValue, (int)permission);
        Assert.True(Enum.IsDefined(permission));
    }

    [Fact]
    public void Catalog_ContainsExactlyTheDocumentedPermissions()
    {
        Assert.Equal(
            new[] { "ConversationsManage", "ConversationsView", "MetricsView", "OrganizationManage", "OrganizationView", "ProductsManage", "ProductsView", "UsersManage", "UsersView" },
            Enum.GetNames<Permission>().Order().ToArray());
    }

    [Theory]
    [InlineData(Permission.ProductsView)]
    [InlineData(Permission.ProductsManage)]
    public void UserPermission_CanBeCreatedForTheProductsPermissions(Permission permission)
    {
        var userId = Guid.NewGuid();

        var userPermission = UserPermission.Create(userId, permission);

        Assert.Equal(userId, userPermission.UserId);
        Assert.Equal(permission, userPermission.Permission);
    }
}
