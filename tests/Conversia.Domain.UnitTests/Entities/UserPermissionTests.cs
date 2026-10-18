using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

public class UserPermissionTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    [Fact]
    public void Create_WithValidInput_ReturnsUserPermissionWithNonEmptyIdAndGivenValues()
    {
        var userPermission = UserPermission.Create(ValidUserId, Permission.UsersView);

        Assert.NotEqual(Guid.Empty, userPermission.Id);
        Assert.Equal(ValidUserId, userPermission.UserId);
        Assert.Equal(Permission.UsersView, userPermission.Permission);
    }

    [Fact]
    public void Create_WithEmptyUserId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => UserPermission.Create(Guid.Empty, Permission.UsersManage));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(999)]
    public void Create_WithUndefinedPermissionValue_ThrowsDomainException(int rawPermission)
    {
        var undefinedPermission = (Permission)rawPermission;

        Assert.Throws<DomainException>(() => UserPermission.Create(ValidUserId, undefinedPermission));
    }
}
