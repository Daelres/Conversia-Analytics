using Conversia.Application.DTOs.Auth;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Conversia.Tests.Shared;
using Moq;

namespace Conversia.Application.UnitTests.Services;

public class PlatformAdminAuthServiceTests
{
    private readonly Mock<IPlatformAdminRepository> _admins = new();
    private readonly Mock<IPasswordHasherService> _hasher = new();
    private readonly Mock<IPlatformAdminTokenService> _tokens = new();

    private readonly FixedTimeProvider _clock = new(new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc));
    private AdminLoginThrottle? _throttle;

    private AdminLoginThrottle Throttle => _throttle ??= new AdminLoginThrottle(_clock);

    private PlatformAdminAuthService CreateSut() => new(_admins.Object, _hasher.Object, _tokens.Object, new AdminLoginDecoy("decoy-hash"), Throttle);

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

    // ---- the decoy ----

    [Fact]
    public void TheDecoyIsBuiltOnceFromTheHasher_NotOnTheFirstUnknownEmail()
    {
        var decoy = AdminLoginDecoy.Create(_hasher.Object);

        Assert.Equal("decoy-hash", decoy.Hash);
        _hasher.Verify(h => h.HashPassword(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task AnUnknownEmailNeverHashesAnything_ItVerifiesAgainstTheDecoyBuiltBefore()
    {
        var sut = CreateSut();

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => sut.LoginAsync(new LoginRequest { Email = "nobody@example.test", Password = "right" }));

        _hasher.Verify(h => h.HashPassword(It.IsAny<string>()), Times.Never);
        _hasher.Verify(h => h.VerifyPassword("decoy-hash", "right"), Times.Once);
    }

    // ---- the brake on one account ----

    private async Task FailAsync(PlatformAdminAuthService sut, string email, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => sut.LoginAsync(new LoginRequest { Email = email, Password = "wrong" }));
        }
    }

    [Fact]
    public async Task AfterFiveFailuresTheAccountIsRefusedBeforeThePasswordIsLookedAt_EvenWithTheRightPassword()
    {
        Stored();
        var sut = CreateSut();
        await FailAsync(sut, "boss@example.test", AdminLoginThrottle.Threshold);
        _hasher.Invocations.Clear();

        var refused = await Assert.ThrowsAsync<LoginThrottledException>(() => sut.LoginAsync(new LoginRequest { Email = "boss@example.test", Password = "right" }));

        Assert.Equal(TimeSpan.FromSeconds(30), refused.RetryAfter);
        _hasher.Verify(h => h.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _admins.Verify(r => r.GetByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(AdminLoginThrottle.Threshold));
    }

    [Fact]
    public async Task AnUnknownEmailIsThrottledExactlyLikeAKnownOne_SoNothingIsEnumerated()
    {
        Stored();
        var sut = CreateSut();
        await FailAsync(sut, "boss@example.test", AdminLoginThrottle.Threshold);
        await FailAsync(sut, "nobody@example.test", AdminLoginThrottle.Threshold);

        var known = await Assert.ThrowsAsync<LoginThrottledException>(() => sut.LoginAsync(new LoginRequest { Email = "boss@example.test", Password = "x" }));
        var unknown = await Assert.ThrowsAsync<LoginThrottledException>(() => sut.LoginAsync(new LoginRequest { Email = "nobody@example.test", Password = "x" }));

        Assert.Equal((known.Message, known.RetryAfter), (unknown.Message, unknown.RetryAfter));
    }

    [Fact]
    public async Task AWaitPassesAndTheNextFailureWaitsLonger_AndASuccessClearsEverything()
    {
        Stored();
        var sut = CreateSut();
        await FailAsync(sut, "boss@example.test", AdminLoginThrottle.Threshold);

        _clock.Advance(TimeSpan.FromSeconds(31));
        await FailAsync(sut, "boss@example.test", 1); // the sixth failure: 60 seconds now
        var longer = await Assert.ThrowsAsync<LoginThrottledException>(() => sut.LoginAsync(new LoginRequest { Email = "boss@example.test", Password = "right" }));
        Assert.Equal(TimeSpan.FromSeconds(60), longer.RetryAfter);

        _clock.Advance(TimeSpan.FromSeconds(61));
        var ok = await sut.LoginAsync(new LoginRequest { Email = "boss@example.test", Password = "right" });
        Assert.Equal("token", ok.Token);
        Assert.Null(Throttle.RetryAfter("boss@example.test"));
        await FailAsync(sut, "boss@example.test", AdminLoginThrottle.Threshold - 1); // the counter started over
        Assert.Null(Throttle.RetryAfter("boss@example.test"));
    }
}
