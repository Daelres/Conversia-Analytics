using Conversia.Application.DTOs.Users;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Moq;

namespace Conversia.Application.UnitTests.Services;

public class UserManagementServiceTests
{
    private const string Email = "member@acme.test";
    private const string Password = "S3cur3-Password!";
    private const string HashedPassword = "hashed-password";

    private readonly Mock<IUserRepository> _userRepository = new();
    private readonly Mock<IPasswordHasherService> _passwordHasherService = new();
    private readonly Guid _callerTenantId = Guid.NewGuid();

    public UserManagementServiceTests()
    {
        _passwordHasherService.Setup(h => h.HashPassword(Password)).Returns(HashedPassword);
    }

    private UserManagementService CreateSut() => new(_userRepository.Object, _passwordHasherService.Object);

    private static CreateUserRequest Request(string email = Email, string password = Password)
        => new() { Email = email, Password = password };

    private void ExpectEmailAvailable(string email = Email)
        => _userRepository.Setup(r => r.ExistsByEmailAsync(email, It.IsAny<CancellationToken>())).ReturnsAsync(false);

    [Fact]
    public async Task CreateUserAsync_WithNewEmail_PersistsANonOwnerInTheCallersTenantWithTheHashedPassword()
    {
        ExpectEmailAvailable();
        User? persisted = null;
        _userRepository.Setup(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback<User, CancellationToken>((u, _) => persisted = u)
            .Returns(Task.CompletedTask);

        var response = await CreateSut().CreateUserAsync(_callerTenantId, Request());

        Assert.NotNull(persisted);
        Assert.Equal(_callerTenantId, persisted!.TenantId);
        Assert.Equal(Email, persisted.Email);
        Assert.False(persisted.IsOwner);
        Assert.Equal(HashedPassword, persisted.PasswordHash);
        Assert.NotEqual(Password, persisted.PasswordHash);
        _userRepository.Verify(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(persisted.Id, response.UserId);
        Assert.Equal(Email, response.Email);
        Assert.False(response.IsOwner);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateUserAsync_WithEmailAlreadyRegistered_ThrowsDuplicateEmailException(bool detectedByPreCheck)
    {
        // Either detection path is valid: an explicit existence check
        // (detectedByPreCheck) or the repository reporting the unique-index
        // violation from AddAsync (a racy pre-check that passed). The store
        // already holds the email in both cases, so AddAsync rejects it too;
        // the service must surface DuplicateEmailException unchanged.
        _userRepository.Setup(r => r.ExistsByEmailAsync(Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(detectedByPreCheck);
        _userRepository.Setup(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DuplicateEmailException());

        await Assert.ThrowsAsync<DuplicateEmailException>(
            () => CreateSut().CreateUserAsync(_callerTenantId, Request()));
    }

    [Fact]
    public async Task CreateUserAsync_WithEmailRejectedByTheDomain_ThrowsDomainExceptionAndWritesNothing()
    {
        // Defence in depth: even if boundary validation were bypassed, a
        // malformed email must end in a controlled DomainException (mapped to
        // 400), never an unhandled crash or a persisted row.
        ExpectEmailAvailable("not-an-email");

        await Assert.ThrowsAsync<DomainException>(
            () => CreateSut().CreateUserAsync(_callerTenantId, Request(email: "not-an-email")));

        _userRepository.Verify(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateUserAsync_WithEmptyCallerTenant_ThrowsDomainExceptionAndWritesNothing()
    {
        ExpectEmailAvailable();

        await Assert.ThrowsAsync<DomainException>(
            () => CreateSut().CreateUserAsync(Guid.Empty, Request()));

        _userRepository.Verify(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
