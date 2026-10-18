namespace Conversia.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL <c>timestamptz</c> keeps microseconds, while <see cref="DateTime"/> keeps 100 ns ticks (what Linux returns for
/// <c>UtcNow</c>). A value stamped at full precision would read back different from the one just returned to the caller, so
/// every timestamp is cut to whole microseconds before it is persisted.
/// </summary>
public static class DatabaseTime
{
    private const long TicksPerMicrosecond = 10;

    public static DateTime Truncate(DateTime value)
        => new(value.Ticks - value.Ticks % TicksPerMicrosecond, value.Kind);
}
