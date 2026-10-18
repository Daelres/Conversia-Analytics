using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Common;

/// <summary>
/// Every instant a Conversia entity stores is UTC and kept to whole microseconds: <c>timestamptz</c> keeps microseconds while
/// <see cref="DateTime"/> keeps 100 ns ticks, so a value persisted at full precision would read back different.
/// </summary>
public static class UtcTime
{
    private const long TicksPerMicrosecond = 10;

    public static DateTime Normalize(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => throw new DomainException("Las fechas deben expresarse en UTC."),
        };

        return new DateTime(utc.Ticks - utc.Ticks % TicksPerMicrosecond, DateTimeKind.Utc);
    }

    public static DateTime? Normalize(DateTime? value) => value is null ? null : Normalize(value.Value);
}
