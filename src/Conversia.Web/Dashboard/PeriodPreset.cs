namespace Conversia.Web.Dashboard;

/// <summary>The periods the dashboard offers. The URL carries the code (<c>?period=7d</c>), never dates, so a shared link always means "the last 7 days" from whoever opens it.</summary>
public enum PeriodPreset
{
    Last7Days,
    Last30Days,
    Last90Days,
    ThisMonth,
    PreviousMonth,
}

/// <summary>A span of local dates, <c>From</c> inclusive and <c>To</c> exclusive, like the API's own.</summary>
public readonly record struct DateRange(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber;

    /// <summary>The span of the same length right before this one (what <c>CONVERSION_DROP</c> compares against, docs/METRICS.md).</summary>
    public DateRange Previous() => new(From.AddDays(-Days), From);
}

public static class PeriodPresets
{
    public const PeriodPreset Default = PeriodPreset.Last30Days;

    public static IReadOnlyList<PeriodPreset> All { get; } = Enum.GetValues<PeriodPreset>();

    public static string Code(PeriodPreset preset) => preset switch
    {
        PeriodPreset.Last7Days => "7d",
        PeriodPreset.Last30Days => "30d",
        PeriodPreset.Last90Days => "90d",
        PeriodPreset.ThisMonth => "month",
        PeriodPreset.PreviousMonth => "last-month",
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    public static string Label(PeriodPreset preset) => preset switch
    {
        PeriodPreset.Last7Days => "Últimos 7 días",
        PeriodPreset.Last30Days => "Últimos 30 días",
        PeriodPreset.Last90Days => "Últimos 90 días",
        PeriodPreset.ThisMonth => "Este mes",
        PeriodPreset.PreviousMonth => "Mes anterior",
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    /// <summary>The preset a query-string code names; anything else (absent, unknown, a number) is the default.</summary>
    public static PeriodPreset Parse(string? code)
        => All.FirstOrDefault(preset => string.Equals(Code(preset), code, StringComparison.Ordinal), Default);

    /// <summary>The local dates of a preset on <paramref name="today"/>; rolling periods include today, "this month" runs from the 1st to today.</summary>
    public static DateRange Resolve(PeriodPreset preset, DateOnly today)
    {
        var tomorrow = today.AddDays(1);
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        return preset switch
        {
            PeriodPreset.Last7Days => new DateRange(today.AddDays(-6), tomorrow),
            PeriodPreset.Last30Days => new DateRange(today.AddDays(-29), tomorrow),
            PeriodPreset.Last90Days => new DateRange(today.AddDays(-89), tomorrow),
            PeriodPreset.ThisMonth => new DateRange(firstOfMonth, tomorrow),
            PeriodPreset.PreviousMonth => new DateRange(firstOfMonth.AddMonths(-1), firstOfMonth),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
    }
}
