using Conversia.Application.DTOs.Auth;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Moq;

namespace Conversia.Application.UnitTests.Services;

public class AuthServiceTests
{
    private readonly Mock<ITenantRepository> _tenantRepository = new(MockBehavior.Strict);
    private readonly Mock<IUserRepository> _userRepository = new(MockBehavior.Strict);
    private readonly Mock<IPasswordHasherService> _passwordHasherService = new(MockBehavior.Strict);
    private readonly Mock<IJwtTokenService> _jwtTokenService = new(MockBehavior.Strict);

    private AuthService CreateSut() => new(
        _tenantRepository.Object,
        _userRepository.Object,
        _passwordHasherService.Object,
        _jwtTokenService.Object);

    [Fact]
    public async Task RegisterTenantAsync_WithNewEmail_CreatesTenantAndOwnerUserAndReturnsToken()
    {
        const string email = "owner@acme.test";
        const string password = "S3cur3-Password!";
        const string tenantName = "Acme Corp";
        const string hashedPassword = "hashed-password";
        const string issuedToken = "issued.jwt.token";

        var request = new RegisterTenantRequest
        {
            TenantName = tenantName,
            Email = email,
            Password = password
        };

        _userRepository.Setup(r => r.ExistsByEmailAsync(email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _passwordHasherService.Setup(h => h.HashPassword(password))
            .Returns(hashedPassword);
        _tenantRepository.Setup(r => r.AddAsync(It.Is<Tenant>(t => t.Name == tenantName), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _userRepository.Setup(r => r.AddAsync(
                It.Is<User>(u => u.Email == email && u.PasswordHash == hashedPassword && u.IsOwner),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _jwtTokenService.Setup(j => j.GenerateToken(It.IsAny<Guid>(), It.IsAny<Guid>(), true))
            .Returns(issuedToken);

        var sut = CreateSut();

        var response = await sut.RegisterTenantAsync(request);

        Assert.Equal(issuedToken, response.Token);
        Assert.True(response.IsOwner);
        Assert.NotEqual(Guid.Empty, response.UserId);
        Assert.NotEqual(Guid.Empty, response.TenantId);

        _tenantRepository.Verify(
            r => r.AddAsync(It.Is<Tenant>(t => t.Name == tenantName), It.IsAny<CancellationToken>()),
            Times.Once);
        _userRepository.Verify(
            r => r.AddAsync(It.Is<User>(u => u.Email == email && u.IsOwner), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RegisterTenantAsync_WithAlreadyRegisteredEmail_ThrowsDuplicateEmailExceptionAndWritesNothing()
    {
        const string email = "existing@acme.test";

        var request = new RegisterTenantRequest
        {
            TenantName = "Acme Corp",
            Email = email,
            Password = "S3cur3-Password!"
        };

        _userRepository.Setup(r => r.ExistsByEmailAsync(email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut();

        await Assert.ThrowsAsync<DuplicateEmailException>(() => sut.RegisterTenantAsync(request));

        _tenantRepository.Verify(
            r => r.AddAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _userRepository.Verify(
            r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WithValidCredentials_ReturnsTokenMatchingTheStoredUser()
    {
        const string email = "owner@acme.test";
        const string password = "S3cur3-Password!";
        const string hashedPassword = "hashed-password";
        const string issuedToken = "issued.jwt.token";

        var tenantId = Guid.NewGuid();
        var existingUser = User.Create(tenantId, email, hashedPassword, isOwner: true);

        var request = new LoginRequest { Email = email, Password = password };

        _userRepository.Setup(r => r.GetByEmailAsync(email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingUser);
        _passwordHasherService.Setup(h => h.VerifyPassword(hashedPassword, password))
            .Returns(true);
        _jwtTokenService.Setup(j => j.GenerateToken(existingUser.Id, tenantId, true))
            .Returns(issuedToken);

        var sut = CreateSut();

        var response = await sut.LoginAsync(request);

        Assert.Equal(issuedToken, response.Token);
        Assert.Equal(existingUser.Id, response.UserId);
        Assert.Equal(tenantId, response.TenantId);
        Assert.True(response.IsOwner);
    }

    [Fact]
    public async Task LoginAsync_WithWrongPassword_ThrowsInvalidCredentialsException()
    {
        const string email = "owner@acme.test";
        const string hashedPassword = "hashed-password";

        var existingUser = User.Create(Guid.NewGuid(), email, hashedPassword, isOwner: false);
        var request = new LoginRequest { Email = email, Password = "wrong-password" };

        _userRepository.Setup(r => r.GetByEmailAsync(email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingUser);
        _passwordHasherService.Setup(h => h.VerifyPassword(hashedPassword, "wrong-password"))
            .Returns(false);

        var sut = CreateSut();

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => sut.LoginAsync(request));
    }

    [Fact]
    public async Task LoginAsync_WithUnknownEmail_ThrowsInvalidCredentialsException()
    {
        const string email = "unknown@acme.test";
        var request = new LoginRequest { Email = email, Password = "whatever-password" };

        _userRepository.Setup(r => r.GetByEmailAsync(email, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var sut = CreateSut();

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => sut.LoginAsync(request));
    }

    [Fact]
    public async Task LoginAsync_UnknownEmailAndWrongPassword_ThrowIndistinguishableExceptions()
    {
        // Security requirement (docs/USERS_TENANTS_PERMISSIONS.md): unknown
        // email and wrong password must be indistinguishable to the caller.
        const string knownEmail = "owner@acme.test";
        const string unknownEmail = "unknown@acme.test";
        const string hashedPassword = "hashed-password";

        var existingUser = User.Create(Guid.NewGuid(), knownEmail, hashedPassword, isOwner: false);

        _userRepository.Setup(r => r.GetByEmailAsync(knownEmail, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingUser);
        _userRepository.Setup(r => r.GetByEmailAsync(unknownEmail, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        _passwordHasherService.Setup(h => h.VerifyPassword(hashedPassword, "wrong-password"))
            .Returns(false);

        var sut = CreateSut();

        var wrongPasswordException = await Assert.ThrowsAsync<InvalidCredentialsException>(
            () => sut.LoginAsync(new LoginRequest { Email = knownEmail, Password = "wrong-password" }));
        var unknownEmailException = await Assert.ThrowsAsync<InvalidCredentialsException>(
            () => sut.LoginAsync(new LoginRequest { Email = unknownEmail, Password = "whatever-password" }));

        Assert.Equal(wrongPasswordException.GetType(), unknownEmailException.GetType());
        Assert.Equal(wrongPasswordException.Message, unknownEmailException.Message);
    }
}
