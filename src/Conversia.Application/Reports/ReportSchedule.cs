using Conversia.Application.Classification.Llm;
using Conversia.Domain.Enums;

namespace Conversia.Application.Reports;

/// <summary>A period of local dates: <see cref="From"/> inclusive, <see cref="To"/> exclusive, like every period of the metrics.</summary>
public sealed record ReportPeriod(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber;
}

/// <summary>One scheduled report: the period it covers and the instant it is due.</summary>
public sealed record ReportOccurrence(ReportPeriod Period, DateTime DueUtc);

/// <summary>
/// When reports are due and what they cover (docs/REPORTS.md, "Schedule"). Pure: local dates and hours of the company's zone (America/Bogota).
/// <list type="bullet">
/// <item><b>Daily</b>: the previous day, sent at the hour.</item>
/// <item><b>Weekly</b>: the previous Monday-to-Sunday week, sent on the chosen day of the following week (Monday by default).</item>
/// <item><b>Monthly</b>: the previous calendar month, sent on the first day.</item>
/// </list>
/// </summary>
public static class ReportSchedule
{
    public static TimeZoneInfo Bogota { get; } = AiQuotaSettings.BogotaTimeZone();

    /// <summary>The most recent occurrence whose send instant is at or before <paramref name="utcNow"/>.</summary>
    public static ReportOccurrence Latest(ReportFrequency frequency, int sendDayOfWeek, int sendHour, DateTime utcNow, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var today = LocalDate(utcNow, zone);
        var boundary = PeriodStartOn(frequency, today);
        var occurrence = At(frequency, boundary, sendDayOfWeek, sendHour, zone);
        return occurrence.DueUtc <= utcNow ? occurrence : At(frequency, Previous(frequency, boundary), sendDayOfWeek, sendHour, zone);
    }

    /// <summary>The first occurrence whose send instant is after <paramref name="utcNow"/> (what the settings page shows as the next send).</summary>
    public static ReportOccurrence Next(ReportFrequency frequency, int sendDayOfWeek, int sendHour, DateTime utcNow, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var boundary = PeriodStartOn(frequency, LocalDate(utcNow, zone));
        var occurrence = At(frequency, boundary, sendDayOfWeek, sendHour, zone);
        return occurrence.DueUtc > utcNow ? occurrence : At(frequency, Following(frequency, boundary), sendDayOfWeek, sendHour, zone);
    }

    /// <summary>The last complete period on <paramref name="utcNow"/>'s local date, whatever the hour (the preview).</summary>
    public static ReportPeriod LastCompleted(ReportFrequency frequency, DateTime utcNow, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var boundary = PeriodStartOn(frequency, LocalDate(utcNow, zone));
        return new ReportPeriod(Previous(frequency, boundary), boundary);
    }

    /// <summary>
    /// Whether an occurrence should be generated now: its instant passed, not longer ago than <paramref name="catchUp"/> (a report of a stale
    /// period is not sent after a long outage) and not before <paramref name="notBeforeUtc"/> (the definition did not exist yet).
    /// </summary>
    public static bool IsDue(ReportOccurrence occurrence, DateTime utcNow, DateTime notBeforeUtc, TimeSpan catchUp)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return occurrence.DueUtc <= utcNow && utcNow - occurrence.DueUtc <= catchUp && occurrence.DueUtc >= notBeforeUtc;
    }

    /// <summary>The UTC instant a local date and hour of <paramref name="zone"/> is; an hour skipped by daylight saving moves to the next valid one.</summary>
    public static DateTime ToUtc(DateOnly date, int hour, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static DateOnly LocalDate(DateTime utc, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    // The first day of the period that contains the date: the day itself, its week's Monday or its month's first day.
    private static DateOnly PeriodStartOn(ReportFrequency frequency, DateOnly date) => frequency switch
    {
        ReportFrequency.Daily => date,
        ReportFrequency.Weekly => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        ReportFrequency.Monthly => new DateOnly(date.Year, date.Month, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, null),
    };

    private static DateOnly Previous(ReportFrequency frequency, DateOnly boundary) => frequency switch
    {
        ReportFrequency.Daily => boundary.AddDays(-1),
        ReportFrequency.Weekly => boundary.AddDays(-7),
        ReportFrequency.Monthly => boundary.AddMonths(-1),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, null),
    };

    private static DateOnly Following(ReportFrequency frequency, DateOnly boundary) => frequency switch
    {
        ReportFrequency.Daily => boundary.AddDays(1),
        ReportFrequency.Weekly => boundary.AddDays(7),
        ReportFrequency.Monthly => boundary.AddMonths(1),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, null),
    };

    // The occurrence sent in the period that starts at boundary: it covers the period before it.
    private static ReportOccurrence At(ReportFrequency frequency, DateOnly boundary, int sendDayOfWeek, int sendHour, TimeZoneInfo zone)
    {
        var sendDate = frequency == ReportFrequency.Weekly ? boundary.AddDays(Math.Clamp(sendDayOfWeek, 1, 7) - 1) : boundary;
        return new ReportOccurrence(new ReportPeriod(Previous(frequency, boundary), boundary), ToUtc(sendDate, Math.Clamp(sendHour, 0, 23), zone));
    }
}
