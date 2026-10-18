using Conversia.Web.Dashboard;
using Conversia.Web.Services;

namespace Conversia.Web.IntegrationTests.Dashboard;

/// <summary>How the dashboard writes numbers and periods. Pure functions: no host, no clock.</summary>
public class DashboardFormatTests
{
    private const string Nbsp = "\u00A0";

    /// <summary>The number and its unit travel together: a regular space between the other words, a non-breaking one before the unit.</summary>
    private static string Glue(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"(\d) (?=(d|h|min|punto|puntos|pts)\b)", "$1" + Nbsp);

    [Theory]
    [InlineData(0, "0")]
    [InlineData(7, "7")]
    [InlineData(1234, "1.234")]
    [InlineData(1234567, "1.234.567")]
    public void Whole_numbers_use_the_spanish_thousands_separator(int number, string expected)
        => Assert.Equal(expected, MetricsFormat.Whole(number));

    [Theory]
    [InlineData(0, "0,0")]
    [InlineData(23.5, "23,5")]
    [InlineData(100, "100,0")]
    [InlineData(33.333, "33,3")]
    public void Percentages_keep_one_decimal_with_a_comma_and_a_non_breaking_space(double value, string expected)
        => Assert.Equal($"{expected}{Nbsp}%", MetricsFormat.Percent(value));

    [Fact]
    public void A_missing_percentage_is_a_dash_never_zero()
        => Assert.Equal("—", MetricsFormat.Percent(null));

    [Theory]
    [InlineData(0.2, "menos de 1 min")]
    [InlineData(0.5, "1 min")]
    [InlineData(12, "12 min")]
    [InlineData(59.4, "59 min")]
    [InlineData(60, "1 h")]
    [InlineData(65, "1 h 05 min")]
    [InlineData(125.2, "2 h 05 min")]
    [InlineData(1440, "1 d")]
    [InlineData(1500, "1 d 1 h")]
    public void Durations_are_written_the_way_a_person_says_them(double minutes, string expected)
        => Assert.Equal(Glue(expected), MetricsFormat.Duration(minutes));

    [Theory]
    [InlineData(null)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    public void A_duration_that_cannot_be_computed_is_a_dash(double? minutes)
        => Assert.Equal("—", MetricsFormat.Duration(minutes));

    [Theory]
    [InlineData(3.5, "+3,5 pts")]
    [InlineData(-2, "−2 pts")]
    [InlineData(0, "0 pts")]
    [InlineData(0.04, "0 pts")]
    [InlineData(-0.04, "0 pts")]
    [InlineData(10, "+10 pts")]
    public void A_change_in_points_carries_its_sign(double points, string expected)
        => Assert.Equal(Glue(expected), MetricsFormat.SignedPoints(points));

    [Theory]
    [InlineData(1, "1 punto")]
    [InlineData(-1, "1 punto")]
    [InlineData(3.5, "3,5 puntos")]
    public void Points_are_always_positive_words(double points, string expected)
        => Assert.Equal(Glue(expected), MetricsFormat.Points(points));

    [Fact]
    public void Dates_and_moments_are_written_in_spanish_without_asking_the_machine_for_its_culture()
    {
        Assert.Equal("18 oct 2026", MetricsFormat.Date(new DateOnly(2026, 10, 18)));
        Assert.Equal("dom 18 oct", MetricsFormat.Day(new DateOnly(2026, 10, 18)));
        Assert.Equal("5 mar, 14:05", MetricsFormat.Moment(new DateTime(2027, 3, 5, 14, 5, 0)));
        Assert.Equal("09:07", MetricsFormat.Clock(new DateTime(2027, 3, 5, 9, 7, 0)));
    }

    // ---- periods ----

    [Theory]
    [InlineData(PeriodPreset.Last7Days, "2026-10-12", "2026-10-19")]
    [InlineData(PeriodPreset.Last30Days, "2026-09-19", "2026-10-19")]
    [InlineData(PeriodPreset.Last90Days, "2026-07-21", "2026-10-19")]
    [InlineData(PeriodPreset.ThisMonth, "2026-10-01", "2026-10-19")]
    [InlineData(PeriodPreset.PreviousMonth, "2026-09-01", "2026-10-01")]
    public void Presets_resolve_to_local_dates_with_an_exclusive_end(PeriodPreset preset, string from, string to)
    {
        var range = PeriodPresets.Resolve(preset, new DateOnly(2026, 10, 18));

        Assert.Equal(DateOnly.Parse(from), range.From);
        Assert.Equal(DateOnly.Parse(to), range.To);
    }

    [Fact]
    public void The_previous_period_is_the_same_length_right_before()
    {
        var range = PeriodPresets.Resolve(PeriodPreset.Last7Days, new DateOnly(2026, 10, 18));

        var previous = range.Previous();

        Assert.Equal(range.Days, previous.Days);
        Assert.Equal(range.From, previous.To);
    }

    [Fact]
    public void The_first_day_of_the_month_is_a_one_day_period_so_far()
        => Assert.Equal(1, PeriodPresets.Resolve(PeriodPreset.ThisMonth, new DateOnly(2026, 10, 1)).Days);

    [Theory]
    [InlineData("7d", PeriodPreset.Last7Days)]
    [InlineData("90d", PeriodPreset.Last90Days)]
    [InlineData("month", PeriodPreset.ThisMonth)]
    [InlineData("last-month", PeriodPreset.PreviousMonth)]
    [InlineData("nonsense", PeriodPreset.Last30Days)]
    [InlineData("", PeriodPreset.Last30Days)]
    [InlineData(null, PeriodPreset.Last30Days)]
    public void The_period_code_in_the_address_falls_back_to_thirty_days(string? code, PeriodPreset expected)
        => Assert.Equal(expected, PeriodPresets.Parse(code));

    [Fact]
    public void Every_preset_has_a_unique_code_and_a_label()
    {
        Assert.Equal(PeriodPresets.All.Count, PeriodPresets.All.Select(PeriodPresets.Code).Distinct().Count());
        Assert.All(PeriodPresets.All, preset => Assert.False(string.IsNullOrWhiteSpace(PeriodPresets.Label(preset))));
    }
}
