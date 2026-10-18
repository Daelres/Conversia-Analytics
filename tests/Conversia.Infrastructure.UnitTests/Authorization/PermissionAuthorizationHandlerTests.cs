using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Moq;

namespace Conversia.Infrastructure.UnitTests.Authorization;

/// <summary>
/// Unit tests for <see cref="PermissionAuthorizationHandler"/> with the
/// repository mocked — no database. Covers docs/USERS_TENANTS_PERMISSIONS.md,
/// "Authorization": owner bypass, permission match/mismatch, and that the
/// check is always scoped to the authenticated caller's own identity (never
/// evaluated against some other user's permission rows).
/// </summary>
public class PermissionAuthorizationHandlerTests
{
    private static ClaimsPrincipal BuildPrincipal(Guid userId, bool isOwner)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("is_owner", isOwner ? "true" : "false")
        }, authenticationType: "TestAuth");

        return new ClaimsPrincipal(identity);
    }

    private static async Task<AuthorizationHandlerContext> RunHandlerAsync(
        Mock<IUserPermissionRepository> repositoryMock, ClaimsPrincipal principal, Permission permission)
    {
        var requirement = new PermissionRequirement(permission);
        var context = new AuthorizationHandlerContext(new[] { requirement }, principal, resource: null);
        var handler = new PermissionAuthorizationHandler(repositoryMock.Object);

        await handler.HandleAsync(context);

        return context;
    }

    [Fact]
    public async Task Owner_WithoutAnyUserPermissionsRow_AlwaysSucceeds()
    {
        var ownerId = Guid.NewGuid();
        var repositoryMock = new Mock<IUserPermissionRepository>(MockBehavior.Strict);
        var principal = BuildPrincipal(ownerId, isOwner: true);

        var context = await RunHandlerAsync(repositoryMock, principal, Permission.UsersManage);

        Assert.True(context.HasSucceeded);
        // Strict mock: any unexpected call (e.g. a DB lookup) throws before
        // this line is even reached, proving owner bypass never hits the
        // repository at all.
        repositoryMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Owner_EvenIfRepositoryWouldDenyThePermission_StillSucceeds()
    {
        var ownerId = Guid.NewGuid();
        var repositoryMock = new Mock<IUserPermissionRepository>(MockBehavior.Strict);
        var principal = BuildPrincipal(ownerId, isOwner: true);

        var context = await RunHandlerAsync(repositoryMock, principal, Permission.UsersManage);

        Assert.True(context.HasSucceeded);
        Assert.False(context.HasFailed);
    }

    [Fact]
    public async Task NonOwner_WithRequiredPermission_Succeeds()
    {
        var userId = Guid.NewGuid();
        var repositoryMock = new Mock<IUserPermissionRepository>();
        repositoryMock
            .Setup(r => r.HasPermissionAsync(userId, Permission.UsersView, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var principal = BuildPrincipal(userId, isOwner: false);

        var context = await RunHandlerAsync(repositoryMock, principal, Permission.UsersView);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task NonOwner_WithoutRequiredPermission_Fails()
    {
        var userId = Guid.NewGuid();
        var repositoryMock = new Mock<IUserPermissionRepository>();
        repositoryMock
            .Setup(r => r.HasPermissionAsync(userId, Permission.UsersManage, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var principal = BuildPrincipal(userId, isOwner: false);

        var context = await RunHandlerAsync(repositoryMock, principal, Permission.UsersManage);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task NonOwner_PermissionCheckIsScopedToTheAuthenticatedCallerOnly_NeverToAnotherUser()
    {
        // Simulates the tenant-isolation invariant at the handler's own
        // boundary: even though *some* user in the system holds this
        // permission, the caller (userA) does not — the handler must not
        // succeed just because a permission row exists somewhere for a
        // different user (which, in a real tenant-scoped query, would be
        // the cross-tenant leak scenario).
        var callingUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var repositoryMock = new Mock<IUserPermissionRepository>();
        repositoryMock
            .Setup(r => r.HasPermissionAsync(callingUserId, Permission.UsersView, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repositoryMock
            .Setup(r => r.HasPermissionAsync(otherUserId, Permission.UsersView, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var principal = BuildPrincipal(callingUserId, isOwner: false);

        var context = await RunHandlerAsync(repositoryMock, principal, Permission.UsersView);

        Assert.False(context.HasSucceeded);
        repositoryMock.Verify(
            r => r.HasPermissionAsync(callingUserId, Permission.UsersView, It.IsAny<CancellationToken>()),
            Times.Once);
        repositoryMock.Verify(
            r => r.HasPermissionAsync(otherUserId, It.IsAny<Permission>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
