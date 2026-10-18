using Conversia.Web.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Dashboard;

/// <summary>The decisions the dashboard makes on top of the API's numbers: the headline picks, the comparison, the breakdown lines and their order.</summary>
public class DashboardModelTests
{
    private static readonly DateOnly To = new(2026, 10, 19);

    private static DashboardData DataOf(MetricsStub stub, Conversia.Application.DTOs.Metrics.ConversionCounts? previous)
        => new(
            new DateRange(To.AddDays(-30), To),
            stub.Summary.Period, stub.Summary, stub.Conversion, stub.FirstResponse, stub.Volume, stub.Alerts, previous, null);

    [Fact]
    public void The_headline_is_picked_from_the_matured_rate_and_the_open_unanswered()
    {
        var stub = MetricsStub.Populated(To);

        var kpis = DashboardKpis.From(DataOf(stub, stub.PreviousConversion));

        Assert.Equal(120, kpis.Conversations);
        Assert.Equal(stub.Conversion.Totals.ConversionRate, kpis.ConversionRate);
        Assert.Equal(12, kpis.UnansweredOpen);
        Assert.Equal(8, kpis.UnansweredClosed);
        Assert.Equal(190, kpis.OldestUnansweredOpenMinutes);
        Assert.Equal("Price", kpis.TopLossReason);
        Assert.Equal(30, kpis.StatedLost);
        Assert.Equal(14, kpis.NoResponseCount);
        Assert.Equal(40, kpis.Following);
    }

    [Fact]
    public void The_variation_is_the_difference_of_the_two_matured_rates_in_points()
    {
        var stub = MetricsStub.Populated(To);

        var kpis = DashboardKpis.From(DataOf(stub, stub.PreviousConversion));

        Assert.Equal(stub.Conversion.Totals.ConversionRate - stub.PreviousConversion!.ConversionRate, kpis.DeltaPoints);
        Assert.Equal(TrendDirection.Up, kpis.Trend);
    }

    [Theory]
    [InlineData(-8, TrendDirection.Down)]
    [InlineData(0.01, TrendDirection.Flat)]
    [InlineData(4, TrendDirection.Up)]
    public void The_trend_follows_the_sign_of_the_variation_and_ignores_noise(double delta, TrendDirection expected)
    {
        var kpis = DashboardKpis.From(DataOf(MetricsStub.Populated(To), MetricsStub.Counts(10, 3, 3, 4, 2, 10, 3, 3)));
        kpis = kpis with { DeltaPoints = delta };

        Assert.Equal(expected, kpis.Trend);
    }

    [Fact]
    public void Without_a_rate_on_either_side_there_is_no_variation_and_no_zero_in_its_place()
    {
        var stub = MetricsStub.Maturing(To);

        var current = DashboardKpis.From(DataOf(stub, MetricsStub.Counts(100, 30, 40, 30, 30, 100, 30, 40)));
        var noPrevious = DashboardKpis.From(DataOf(MetricsStub.Populated(To), previous: null));

        Assert.Null(current.ConversionRate);
        Assert.True(current.InMaturation);
        Assert.Null(current.DeltaPoints);
        Assert.Equal(TrendDirection.Unknown, current.Trend);
        Assert.Null(noPrevious.DeltaPoints);
        Assert.Equal(TrendDirection.Unknown, noPrevious.Trend);
    }

    [Fact]
    public void A_period_without_stated_losses_has_no_main_reason()
    {
        var kpis = DashboardKpis.From(DataOf(MetricsStub.Quiet(To), null));

        Assert.Null(kpis.TopLossReason);
        Assert.Null(kpis.TopLossSharePercent);
    }

    [Fact]
    public void Breakdown_lines_join_conversion_and_response_and_name_the_unassigned_bucket()
    {
        var rows = BreakdownRow.ByAdvisor(DataOf(MetricsStub.Populated(To), null));

        Assert.Equal(["Ana Torres", "Luis Pérez", BreakdownRow.UnassignedName], rows.Select(r => r.Name));
        var ana = rows[0];
        Assert.Equal(60, ana.Total);
        Assert.Equal(3, ana.UnansweredOpen);
        Assert.Equal(8, ana.MedianMinutes);
    }

    [Fact]
    public void Sources_are_matched_by_name_and_shown_in_spanish()
    {
        var rows = BreakdownRow.BySource(DataOf(MetricsStub.Populated(To), null));

        Assert.Equal(["Anuncios", "Orgánico", "Desconocido"], rows.Select(r => r.Name));
        Assert.All(rows, row => Assert.NotNull(row.MedianMinutes));
    }

    [Fact]
    public void A_small_sample_is_flagged_and_a_maturing_group_says_so_instead()
    {
        var rows = BreakdownRow.ByBranch(DataOf(MetricsStub.Populated(To), null));
        var maturing = BreakdownRow.ByBranch(DataOf(MetricsStub.Maturing(To), null));

        Assert.Equal(["Centro", "Norte"], rows.Select(r => r.Name));
        Assert.False(rows[0].LowSample); // 70 matured + decided
        Assert.False(rows[1].LowSample);
        var smallAdvisor = BreakdownRow.ByAdvisor(DataOf(MetricsStub.Populated(To), null)).Single(r => r.Name == "Luis Pérez");
        Assert.True(smallAdvisor.LowSample); // 12 decided
        Assert.All(maturing, row => Assert.True(row.InMaturation && !row.LowSample));
    }

    [Fact]
    public void Sorting_puts_rows_without_a_value_last_in_either_direction()
    {
        var rows = new[]
        {
            Row("a", conversion: 10), Row("b", conversion: null), Row("c", conversion: 40),
        };

        Assert.Equal(["c", "a", "b"], BreakdownRow.Sorted(rows, "-conversion").Select(r => r.Name));
        Assert.Equal(["a", "c", "b"], BreakdownRow.Sorted(rows, "conversion").Select(r => r.Name));
        Assert.Equal(["a", "b", "c"], BreakdownRow.Sorted(rows, null).Select(r => r.Name));
    }

    [Fact]
    public void An_unknown_sort_is_ignored_and_a_click_flips_the_order()
    {
        Assert.Null(BreakdownRow.ValidSort("password"));
        Assert.Null(BreakdownRow.ValidSort("-drop table"));
        Assert.Equal("-total", BreakdownRow.ValidSort("-total"));
        Assert.Equal("-conversion", BreakdownRow.NextSort(null, "conversion"));
        Assert.Equal("conversion", BreakdownRow.NextSort("-conversion", "conversion"));
        Assert.Equal("name", BreakdownRow.NextSort(null, "name"));
    }

    private static BreakdownRow Row(string name, double? conversion)
        => new(name, name, 10, 1, 10, conversion, null, null, 0, InMaturation: false, LowSample: false);
}
