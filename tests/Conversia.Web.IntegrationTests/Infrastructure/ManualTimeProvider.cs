namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// A clock that only moves when a test says so, for the time-based rules of the Web (the storm control's one-minute window).
/// It starts at the real current time, so a code made from it still carries today's date. Both the wall clock and the monotonic
/// timestamp follow <see cref="Advance"/>, so it works whichever of the two an implementation reads. Timers are the system's.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
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
