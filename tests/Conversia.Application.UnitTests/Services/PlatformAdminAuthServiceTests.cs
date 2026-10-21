using Conversia.Application.DTOs.Auth;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Moq;

namespace Conversia.Application.UnitTests.Services;

public class PlatformAdminAuthServiceTests
{
    private readonly Mock<IPlatformAdminRepository> _admins = new();
    private readonly Mock<IPasswordHasherService> _hasher = new();
    private readonly Mock<IPlatformAdminTokenService> _tokens = new();

    private PlatformAdminAuthService CreateSut() => new(_admins.Object, _hasher.Object, _tokens.Object);

    public PlatformAdminAuthServiceTests()
    {
        _hasher.Setup(h => h.HashPassword(It.IsAny<string>())).Returns("decoy-hash");
        _hasher.Setup(h => h.VerifyPassword("stored-hash", "right")).Returns(true);
        _hasher.Setup(h => h.VerifyPassword(It.Is<string>(s => s != "stored-hash"), It.IsAny<string>())).Returns(false);
        _hasher.Setup(h => h.VerifyPassword("stored-hash", It.Is<string>(p => p != "right"))).Returns(false);
        _tokens.Setup(t => t.Issue(It.IsAny<Guid>())).Returns(("token", new DateTime(2026, 10, 20, 13, 0, 0, DateTimeKind.Utc)));
    }

    private PlatformAdmin Stored(bool active = true)
    {
        var admin = PlatformAdmin.Create("boss@example.test", "stored-hash");
        if (!active)
        {
            admin.Deactivate();
        }

        _admins.Setup(r => r.GetByEmailAsync("boss@example.test", It.IsAny<CancellationToken>())).ReturnsAsync(admin);
        return admin;
    }

    [Fact]
    public async Task RightCredentials_IssueATokenForTheAdmin()
    {
        var admin = Stored();

        var response = await CreateSut().LoginAsync(new LoginRequest { Email = " boss@example.test ", Password = "right" });

        Assert.Equal(("token", admin.Id), (response.Token, response.AdminId));
    }

    [Fact]
    public async Task WrongPassword_UnknownEmailAndDeactivatedAdmin_AllThrowTheSameException_AndAllCostOneVerification()
    {
        Stored(active: false);
        var sut = CreateSut();

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => sut.LoginAsync(new LoginRequest { Email = "boss@example.test", Password = "right" })); // deactivated
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => sut.LoginAsync(new LoginRequest { Email = "nobody@example.test", Password = "right" })); // unknown
        Stored();
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => sut.LoginAsync(new LoginRequest { Email = "boss@example.test", Password = "wrong" })); // wrong password

        _hasher.Verify(h => h.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(3));
        _tokens.Verify(t => t.Issue(It.IsAny<Guid>()), Times.Never);
    }
}
