using Conversia.Application.Exceptions;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Moq;

namespace Conversia.Application.UnitTests.Services;

public class PermissionServiceRevokeTests
{
    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<IUserPermissionRepository> _userPermissionRepository = new();
    private readonly Guid _callerTenantId = Guid.NewGuid();

    private PermissionService CreateSut() => new(_userRepository.Object, _userPermissionRepository.Object);

    private User UserInTenant(Guid tenantId) => User.Create(tenantId, "member@acme.test", "hash", isOwner: false);

    private void VerifyNothingRemoved()
        => _userPermissionRepository.Verify(
            r => r.RemoveAsync(It.IsAny<Guid>(), It.IsAny<Permission>(), It.IsAny<CancellationToken>()), Times.Never);

    [Theory]
    [InlineData(Permission.UsersView)]
    [InlineData(Permission.UsersManage)]
    public async Task RevokePermissionAsync_ForUserInCallersTenant_RemovesExactlyThatPermission(Permission permission)
    {
        var target = UserInTenant(_callerTenantId);
        _userRepository.Setup(r => r.GetByIdAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        _userPermissionRepository.Setup(r => r.RemoveAsync(target.Id, permission, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await CreateSut().RevokePermissionAsync(_callerTenantId, target.Id, permission);

        _userPermissionRepository.Verify(
            r => r.RemoveAsync(target.Id, permission, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RevokePermissionAsync_WhenTheTargetDoesNotHoldThePermission_CompletesWithoutError()
    {
        // Idempotent revoke: the repository treats "nothing to remove" as a
        // no-op, so the service must not turn it into a failure.
        var target = UserInTenant(_callerTenantId);
        _userRepository.Setup(r => r.GetByIdAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        _userPermissionRepository.Setup(r => r.RemoveAsync(target.Id, Permission.UsersManage, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var exception = await Record.ExceptionAsync(
            () => CreateSut().RevokePermissionAsync(_callerTenantId, target.Id, Permission.UsersManage));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RevokePermissionAsync_ForNonexistentUser_ThrowsUserNotFoundExceptionAndRemovesNothing()
    {
        var missingUserId = Guid.NewGuid();
        _userRepository.Setup(r => r.GetByIdAsync(missingUserId, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        await Assert.ThrowsAsync<UserNotFoundException>(
            () => CreateSut().RevokePermissionAsync(_callerTenantId, missingUserId, Permission.UsersView));

        VerifyNothingRemoved();
    }

    [Fact]
    public async Task RevokePermissionAsync_ForUserInAnotherTenant_ThrowsUserNotFoundExceptionAndRemovesNothing()
    {
        var foreignUser = UserInTenant(Guid.NewGuid());
        _userRepository.Setup(r => r.GetByIdAsync(foreignUser.Id, It.IsAny<CancellationToken>())).ReturnsAsync(foreignUser);

        await Assert.ThrowsAsync<UserNotFoundException>(
            () => CreateSut().RevokePermissionAsync(_callerTenantId, foreignUser.Id, Permission.UsersView));

        VerifyNothingRemoved();
    }

    [Fact]
    public async Task RevokePermissionAsync_NonexistentAndCrossTenantTargets_AreIndistinguishable()
    {
        // Anti-enumeration contract, identical to AssignPermissionAsync.
        var missingUserId = Guid.NewGuid();
        var foreignUser = UserInTenant(Guid.NewGuid());
        _userRepository.Setup(r => r.GetByIdAsync(missingUserId, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        _userRepository.Setup(r => r.GetByIdAsync(foreignUser.Id, It.IsAny<CancellationToken>())).ReturnsAsync(foreignUser);
        var sut = CreateSut();

        var missing = await Assert.ThrowsAsync<UserNotFoundException>(
            () => sut.RevokePermissionAsync(_callerTenantId, missingUserId, Permission.UsersView));
        var crossTenant = await Assert.ThrowsAsync<UserNotFoundException>(
            () => sut.RevokePermissionAsync(_callerTenantId, foreignUser.Id, Permission.UsersView));

        Assert.Equal(missing.GetType(), crossTenant.GetType());
        Assert.Equal(missing.Message, crossTenant.Message);
    }
}
