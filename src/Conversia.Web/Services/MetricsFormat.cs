using System.Globalization;
using Conversia.Domain.Common;

namespace Conversia.Web.Services;

/// <summary>
/// How the dashboard writes numbers: Spanish separators (<c>1.234,5</c>), percentages as the API sends them (0 to 100, one decimal, so
/// <c>23,5 %</c> with a non-breaking space) and durations the way a person says them (<c>12 min</c>, <c>1 h 05 min</c>). A value that
/// cannot be computed is a dash, never a zero (docs/METRICS.md).
/// </summary>
public static class MetricsFormat
{
    private const string NonBreakingSpace = "\u00A0";

    /// <summary>What is shown when there is nothing to compute (no decided conversations, no answered ones).</summary>
    public const string NoData = "—";

    private static CultureInfo Culture => SpanishCulture.Info;

    public static string Whole(int number) => number.ToString("N0", Culture);

    public static string Whole(long number) => number.ToString("N0", Culture);

    /// <summary>A percentage already in 0 to 100 (<c>conversionRate</c>), one decimal: "23,5 %".</summary>
    public static string Percent(double? percent)
        => percent is { } value ? $"{value.ToString("N1", Culture)}{NonBreakingSpace}%" : NoData;

    /// <summary>"12 min", "1 h 05 min", "2 h", "3 d 4 h"; under a minute "menos de 1 min".</summary>
    public static string Duration(double? minutes)
    {
        if (minutes is not { } value || double.IsNaN(value) || value < 0)
        {
            return NoData;
        }

        if (value < 0.5)
        {
            return $"menos de 1{NonBreakingSpace}min";
        }

        var total = (long)Math.Round(value, MidpointRounding.AwayFromZero);
        var days = total / 1440;
        var hours = total % 1440 / 60;
        var rest = total % 60;

        if (days > 0)
        {
            return hours == 0 ? $"{days}{NonBreakingSpace}d" : $"{days}{NonBreakingSpace}d {hours}{NonBreakingSpace}h";
        }

        if (hours > 0)
        {
            return rest == 0 ? $"{hours}{NonBreakingSpace}h" : $"{hours}{NonBreakingSpace}h {rest:00}{NonBreakingSpace}min";
        }

        return $"{rest}{NonBreakingSpace}min";
    }

    /// <summary>The distance between two rates in percentage points, always positive: "3,5 puntos", "1 punto".</summary>
    public static string Points(double points)
    {
        var magnitude = Math.Abs(points);
        return Math.Round(magnitude, 1) == 1 ? $"1{NonBreakingSpace}punto" : $"{magnitude.ToString("N1", Culture)}{NonBreakingSpace}puntos";
    }

    /// <summary>A change in percentage points with its direction written out: "+3,5 pts", "−2 pts", "0 pts" (a real minus sign, so it is not read as a hyphen).</summary>
    public static string SignedPoints(double points)
    {
        var rounded = Math.Round(points, 1, MidpointRounding.AwayFromZero);
        var magnitude = Math.Abs(rounded).ToString("0.#", Culture);
        return rounded > 0 ? $"+{magnitude}{NonBreakingSpace}pts" : rounded < 0 ? $"−{magnitude}{NonBreakingSpace}pts" : $"0{NonBreakingSpace}pts";
    }

    /// <summary>"1 conversación", "1.234 conversaciones".</summary>
    public static string Conversations(int count)
        => count == 1 ? "1 conversación" : $"{Whole(count)} conversaciones";

    // Written out instead of asked of the culture, so a host without ICU data (invariant globalization) still shows Spanish.
    private static readonly string[] Months = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"];

    private static readonly string[] Weekdays = ["dom", "lun", "mar", "mié", "jue", "vie", "sáb"];

    /// <summary>A local date the way the interface writes it: "18 oct 2026".</summary>
    public static string Date(DateOnly date) => $"{date.Day} {Months[date.Month - 1]} {date.Year}";

    /// <summary>A day without the year, with its weekday: "dom 18 oct".</summary>
    public static string Day(DateOnly date) => $"{Weekdays[(int)date.DayOfWeek]} {date.Day} {Months[date.Month - 1]}";

    /// <summary>A moment already in the tenant's time zone: "18 oct, 14:05". The API sends UTC, so the caller converts first.</summary>
    public static string Moment(DateTime moment) => $"{moment.Day} {Months[moment.Month - 1]}, {moment:HH:mm}";

    /// <summary>A clock time already in the tenant's time zone: "14:05".</summary>
    public static string Clock(DateTime moment) => moment.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
