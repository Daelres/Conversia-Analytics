using Conversia.Application.ErrorLogging;

namespace Conversia.Application.UnitTests.ErrorLogging;

/// <summary>
/// Storm control: after <c>MaxPerFingerprintPerMinute</c> persisted occurrences of one fingerprint within a minute, further
/// ones are refused. A sliding window per fingerprint, driven here by a clock the test advances by hand.
/// </summary>
public class ErrorStormGuardTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void The_first_occurrences_up_to_the_limit_are_let_through_and_the_next_one_is_refused()
    {
        var clock = new ManualTimeProvider();
        var guard = new ErrorStormGuard(3, clock);

        Assert.True(guard.TryAcquire("aaaaaaaaaaaaaaaa"));
        Assert.True(guard.TryAcquire("aaaaaaaaaaaaaaaa"));
        Assert.True(guard.TryAcquire("aaaaaaaaaaaaaaaa"));
        Assert.False(guard.TryAcquire("aaaaaaaaaaaaaaaa"));
        Assert.False(guard.TryAcquire("aaaaaaaaaaaaaaaa"));
    }

    [Fact]
    public void A_limit_of_one_lets_exactly_one_occurrence_through_per_minute()
    {
        var clock = new ManualTimeProvider();
        var guard = new ErrorStormGuard(1, clock);

        Assert.True(guard.TryAcquire("f"));
        Assert.False(guard.TryAcquire("f"));

        clock.Advance(Second * 61);

        Assert.True(guard.TryAcquire("f"));
        Assert.False(guard.TryAcquire("f"));
    }

    [Fact]
    public void Each_fingerprint_has_its_own_budget()
    {
        var clock = new ManualTimeProvider();
        var guard = new ErrorStormGuard(2, clock);

        Assert.True(guard.TryAcquire("storm"));
        Assert.True(guard.TryAcquire("storm"));
        Assert.False(guard.TryAcquire("storm"));

        Assert.True(guard.TryAcquire("other"));
        Assert.True(guard.TryAcquire("other"));
        Assert.False(guard.TryAcquire("other"));
        Assert.True(guard.TryAcquire("third"));
        Assert.False(guard.TryAcquire("storm"));
    }

    [Fact]
    public void Fingerprints_are_compared_exactly()
    {
        var guard = new ErrorStormGuard(1, new ManualTimeProvider());

        Assert.True(guard.TryAcquire("abcdef0123456789"));
        Assert.True(guard.TryAcquire("abcdef0123456788"));
        Assert.True(guard.TryAcquire("ABCDEF0123456789"));
        Assert.False(guard.TryAcquire("abcdef0123456789"));
    }

    [Fact]
    public void The_budget_returns_once_the_minute_has_passed()
    {
        var clock = new ManualTimeProvider();
        var guard = new ErrorStormGuard(3, clock);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(guard.TryAcquire("f"));
        }

        clock.Advance(Second * 59);
        Assert.False(guard.TryAcquire("f"));

        clock.Advance(Second * 2); // 61 s after the first three
        Assert.True(guard.TryAcquire("f"));
        Assert.True(guard.TryAcquire("f"));
        Assert.True(guard.TryAcquire("f"));
        Assert.False(guard.TryAcquire("f"));
    }

    [Fact]
    public void The_window_slides_instead_of_resetting_all_at_once()
    {
        var clock = new ManualTimeProvider();
        var guard = new ErrorStormGuard(2, clock);

        Assert.True(guard.TryAcquire("f"));      // t = 0
        clock.Advance(Second * 30);
        Assert.True(guard.TryAcquire("f"));      // t = 30
        Assert.False(guard.TryAcquire("f"));

        clock.Advance(Second * 31);              // t = 61: only the first has left the window
        Assert.True(guard.TryAcquire("f"));
        Assert.False(guard.TryAcquire("f"));

        clock.Advance(Second * 30);              // t = 91: the second has left too
        Assert.True(guard.TryAcquire("f"));
        Assert.False(guard.TryAcquire("f"));
    }

    [Fact]
    public void Refused_occurrences_do_not_extend_the_block()
    {
        var clock = new ManualTimeProvider();
        var guard = new ErrorStormGuard(2, clock);
        Assert.True(guard.TryAcquire("f"));
        Assert.True(guard.TryAcquire("f"));

        for (var i = 0; i < 10; i++)
        {
            clock.Advance(Second * 5);
            Assert.False(guard.TryAcquire("f"));
        }

        clock.Advance(Second * 11); // 61 s after the two that counted; the 10 refusals in between must not count
        Assert.True(guard.TryAcquire("f"));
        Assert.True(guard.TryAcquire("f"));
        Assert.False(guard.TryAcquire("f"));
    }

    [Fact]
    public void A_quiet_fingerprint_is_never_refused()
    {
        var clock = new ManualTimeProvider();
        var guard = new ErrorStormGuard(1, clock);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(guard.TryAcquire("f"), $"occurrence {i}");
            clock.Advance(Second * 61);
        }
    }

    [Fact]
    public void An_empty_fingerprint_is_handled_like_any_other()
    {
        var guard = new ErrorStormGuard(1, new ManualTimeProvider());

        var failure = Record.Exception(() => guard.TryAcquire(string.Empty));

        Assert.Null(failure);
        Assert.False(guard.TryAcquire(string.Empty));
    }

    [Fact]
    public void Concurrent_callers_never_get_more_than_the_limit_through()
    {
        var clock = new ManualTimeProvider();
        var guard = new ErrorStormGuard(20, clock);
        var allowedOne = 0;
        var allowedTwo = 0;

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 32 }, _ =>
        {
            for (var i = 0; i < 50; i++)
            {
                if (guard.TryAcquire("one"))
                {
                    Interlocked.Increment(ref allowedOne);
                }

                if (guard.TryAcquire("two"))
                {
                    Interlocked.Increment(ref allowedTwo);
                }
            }
        });

        Assert.Equal(20, allowedOne);
        Assert.Equal(20, allowedTwo);
    }

    [Fact]
    public void Many_distinct_fingerprints_do_not_interfere_with_each_other()
    {
        var guard = new ErrorStormGuard(1, new ManualTimeProvider());

        for (var i = 0; i < 5000; i++)
        {
            Assert.True(guard.TryAcquire($"fingerprint-{i}"), $"first occurrence of {i}");
        }

        for (var i = 0; i < 5000; i++)
        {
            Assert.False(guard.TryAcquire($"fingerprint-{i}"), $"second occurrence of {i}");
        }
    }

    /// <summary>A clock that only moves when told to, usable whether the guard reads the wall clock or the monotonic timestamp.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override long GetTimestamp()
        {
            lock (_gate)
            {
                return _timestamp;
            }
        }

        public void Advance(TimeSpan by)
        {
            lock (_gate)
            {
                _now += by;
                _timestamp += by.Ticks;
            }
        }
    }
}
