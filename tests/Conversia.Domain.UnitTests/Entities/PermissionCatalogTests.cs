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
            new[] { "ConversationsManage", "ConversationsView", "MetricsView", "OrganizationManage", "OrganizationView", "UsersManage", "UsersView" },
            Enum.GetNames<Permission>().Order().ToArray());
    }

    [Fact]
    public void TheRetiredProductsNumbers_AreNotDefinedAndMustNotBeReused()
    {
        // 3 and 4 were ProductsView and ProductsManage; databases may still hold grants with those numbers until the RemoveProducts migration runs.
        Assert.False(Enum.IsDefined((Permission)3));
        Assert.False(Enum.IsDefined((Permission)4));
        Assert.Equal(new[] { 1, 2, 5, 6, 7, 8, 9 }, Enum.GetValues<Permission>().Select(p => (int)p).Order().ToArray());
    }

    [Theory]
    [InlineData(Permission.UsersView)]
    [InlineData(Permission.MetricsView)]
    public void UserPermission_CanBeCreatedForACatalogPermission(Permission permission)
    {
        var userId = Guid.NewGuid();

        var userPermission = UserPermission.Create(userId, permission);

        Assert.Equal(userId, userPermission.UserId);
        Assert.Equal(permission, userPermission.Permission);
    }
}
