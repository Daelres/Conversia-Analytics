namespace Conversia.Tests.Shared;

/// <summary>
/// A clock that stands where a test puts it and only moves when the test says so, so time-based rules never depend on the day the suite
/// runs. The wall clock and the monotonic timestamp follow <see cref="Advance"/>. Timers are the system's.
/// </summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now;
    private long _timestamp = 1_000_000;

    public FixedTimeProvider(DateTime utcNow)
    {
        _now = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

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
