using Conversia.Application.Services;
using Conversia.Tests.Shared;

namespace Conversia.Application.UnitTests.Services;

/// <summary>The per-account brake of the administrator's sign-in, with a clock the tests move.</summary>
public class AdminLoginThrottleTests
{
    private readonly FixedTimeProvider _clock = new(new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc));

    private AdminLoginThrottle Create() => new(_clock);

    private static void Fail(AdminLoginThrottle throttle, string email, int times)
    {
        for (var i = 0; i < times; i++)
        {
            throttle.RecordFailure(email);
        }
    }

    [Fact]
    public void BelowTheThresholdNothingWaits()
    {
        var throttle = Create();

        Fail(throttle, "boss@example.test", AdminLoginThrottle.Threshold - 1);

        Assert.Null(throttle.RetryAfter("boss@example.test"));
    }

    [Theory]
    [InlineData(5, 30)]
    [InlineData(6, 60)]
    [InlineData(7, 120)]
    [InlineData(8, 240)]
    [InlineData(9, 480)]
    [InlineData(10, 900)]
    [InlineData(25, 900)]
    [InlineData(500, 900)]
    public void EachFailurePastTheThresholdDoublesTheWait_UpToTheCap(int failures, int seconds)
    {
        var throttle = Create();

        Fail(throttle, "boss@example.test", failures);

        Assert.Equal(TimeSpan.FromSeconds(seconds), throttle.RetryAfter("boss@example.test"));
    }

    [Fact]
    public void TheWaitCountsDownAndEnds()
    {
        var throttle = Create();
        Fail(throttle, "boss@example.test", 5);

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(20), throttle.RetryAfter("boss@example.test"));

        _clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(throttle.RetryAfter("boss@example.test"));
    }

    [Fact]
    public void FailuresAreForgottenAfterTheWindowWithoutAny()
    {
        var throttle = Create();
        Fail(throttle, "boss@example.test", 4);

        _clock.Advance(AdminLoginThrottle.Window + TimeSpan.FromSeconds(1));
        Fail(throttle, "boss@example.test", 1); // would be the fifth, but the four before are gone

        Assert.Null(throttle.RetryAfter("boss@example.test"));
        Assert.Equal(1, throttle.TrackedAccounts);
    }

    [Fact]
    public void AnExpiredAccountIsDroppedWhenAskedAbout()
    {
        var throttle = Create();
        Fail(throttle, "boss@example.test", 5);

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Null(throttle.RetryAfter("boss@example.test"));
        Assert.Equal(0, throttle.TrackedAccounts);
    }

    [Fact]
    public void AccountsAreIndependent_AndTheEmailIsNormalized()
    {
        var throttle = Create();
        Fail(throttle, " Boss@Example.TEST ", 5);

        Assert.NotNull(throttle.RetryAfter("boss@example.test"));
        Assert.Null(throttle.RetryAfter("other@example.test"));
    }

    [Fact]
    public void ASuccessResetsTheAccount()
    {
        var throttle = Create();
        Fail(throttle, "boss@example.test", 5);

        throttle.Reset("boss@example.test");

        Assert.Null(throttle.RetryAfter("boss@example.test"));
        Assert.Equal(0, throttle.TrackedAccounts);
    }

    [Fact]
    public void MemoryIsBounded_ByAccountCountAndByKeyLength()
    {
        var throttle = Create();

        for (var i = 0; i < AdminLoginThrottle.MaxAccounts + 500; i++)
        {
            throttle.RecordFailure($"user{i}@example.test");
            _clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.Equal(AdminLoginThrottle.MaxAccounts, throttle.TrackedAccounts);

        // Two emails that differ only after the cap are one account: the key never grows with what an attacker types.
        var long1 = new string('a', AdminLoginThrottle.MaxKeyLength) + "1";
        var long2 = new string('a', AdminLoginThrottle.MaxKeyLength) + "2";
        Fail(throttle, long1, 3);
        Fail(throttle, long2, 2);
        Assert.NotNull(throttle.RetryAfter(long1));
    }

    [Fact]
    public void WhenFullTheExpiredAreEvictedFirst_AndTheOldestAfterThem()
    {
        var throttle = Create();
        Fail(throttle, "victim@example.test", 5);
        for (var i = 0; i < AdminLoginThrottle.MaxAccounts - 1; i++)
        {
            throttle.RecordFailure($"spray{i}@example.test");
        }

        _clock.Advance(AdminLoginThrottle.Window + TimeSpan.FromMinutes(1)); // everything expired
        throttle.RecordFailure("new@example.test");

        Assert.Equal(1, throttle.TrackedAccounts);
        Assert.Null(throttle.RetryAfter("victim@example.test"));
    }

    [Fact]
    public async Task ItIsSafeUnderConcurrentFailures()
    {
        var throttle = Create();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() => throttle.RecordFailure(i % 2 == 0 ? "a@example.test" : "b@example.test"))));

        Assert.NotNull(throttle.RetryAfter("a@example.test"));
        Assert.NotNull(throttle.RetryAfter("b@example.test"));
        Assert.Equal(2, throttle.TrackedAccounts);
    }
}
