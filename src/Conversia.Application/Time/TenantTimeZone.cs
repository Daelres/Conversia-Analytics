namespace Conversia.Application.Time;

/// <summary>
/// The time zone the business thinks in (<c>Metrics:TimeZone</c>, default <c>America/Bogota</c>): "today", the start of a day and the
/// date ranges of the conversation list and the metrics are local dates of this zone, while everything stored stays UTC.
/// </summary>
public sealed class TenantTimeZone
{
    public const string DefaultId = "America/Bogota";

    public TenantTimeZone(string id)
    {
        Id = id;
        Zone = TimeZoneInfo.FindSystemTimeZoneById(id);
    }

    /// <summary>The IANA name, which is also what PostgreSQL's <c>AT TIME ZONE</c> expects.</summary>
    public string Id { get; }

    public TimeZoneInfo Zone { get; }

    /// <summary>The local calendar date at <paramref name="utcNow"/>.</summary>
    public DateOnly LocalDate(DateTime utcNow)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), Zone));

    /// <summary>
    /// The UTC instant at which the local <paramref name="date"/> begins. When midnight does not exist (a daylight-saving gap) the day
    /// begins at the first valid minute after the gap; when it happens twice, at its first occurrence.
    /// </summary>
    public DateTime StartOfDayUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (Zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = Zone.IsAmbiguousTime(local) ? Zone.GetAmbiguousTimeOffsets(local).Max() : Zone.GetUtcOffset(local);
        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }
}
