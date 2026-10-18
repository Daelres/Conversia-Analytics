using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Metrics;

namespace Conversia.Application.UnitTests.DTOs;

public class MetricsQueryValidationTests
{
    private static List<ValidationResult> Validate(MetricsQuery query) => FixedClockValidation.Validate(query);

    private static readonly DateOnly Day = new(2026, 1, 1);

    [Fact]
    public void AnEmptyQuery_IsValid_AndMeansTheLast30Days()
        => Assert.Empty(Validate(new MetricsQuery()));

    [Theory]
    [InlineData(1, true)]
    [InlineData(31, true)]
    [InlineData(366, true)] // the longest range
    [InlineData(367, false)]
    [InlineData(1000, false)]
    public void TheRange_IsAtMost366Days(int days, bool valid)
        => Assert.Equal(valid, Validate(new MetricsQuery { From = Day, To = Day.AddDays(days) }).Count == 0);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30)]
    public void FromMustBeBeforeTo(int days)
    {
        var errors = Validate(new MetricsQuery { From = Day, To = Day.AddDays(days) });

        Assert.Single(errors);
        Assert.Equal("El campo Desde debe ser anterior al campo Hasta.", errors[0].ErrorMessage);
    }

    [Fact]
    public void TheRangeError_IsSpanish_AndNamesTheLimit()
        => Assert.Equal("El rango de fechas no puede superar los 366 días.", Validate(new MetricsQuery { From = Day, To = Day.AddDays(400) }).Single().ErrorMessage);

    [Fact]
    public void OneEndAlone_IsAlwaysFine()
    {
        Assert.Empty(Validate(new MetricsQuery { From = Day }));
        Assert.Empty(Validate(new MetricsQuery { To = Day }));
    }

    [Theory]
    [InlineData("Ads", true)]
    [InlineData("ads", true)]
    [InlineData("Organic", true)]
    [InlineData("Unknown", true)]
    [InlineData("", true)]
    [InlineData("2", false)]
    [InlineData("Radio", false)]
    public void TheSource_IsAMemberName(string source, bool valid)
        => Assert.Equal(valid, Validate(new MetricsQuery { Source = source }).Count == 0);

    [Fact]
    public void AnAdvisor_AndUnassigned_CannotBeCombined()
    {
        Assert.NotEmpty(Validate(new MetricsQuery { AdvisorId = Guid.NewGuid(), Unassigned = true }));
        Assert.Empty(Validate(new MetricsQuery { AdvisorId = Guid.NewGuid(), Unassigned = false }));
        Assert.Empty(Validate(new MetricsQuery { Unassigned = true }));
    }

    // ---- Each date on its own: a plausible window, whatever the other end is ----------------------------------------------

    [Theory]
    [InlineData("2015-01-01", true)] // the first day allowed
    [InlineData("2014-12-31", false)]
    [InlineData("0001-01-05", false)]
    [InlineData("2027-10-19", true)] // today (2026-10-18) plus 366 days
    [InlineData("2027-10-20", false)]
    [InlineData("9999-12-31", false)]
    public void FromAlone_MustBeInsideTheWindow(string date, bool valid)
    {
        var errors = Validate(new MetricsQuery { From = DateOnly.Parse(date) });

        Assert.Equal(valid, errors.Count == 0);
        if (!valid)
        {
            Assert.Equal("El campo Desde debe ser una fecha entre el 2015-01-01 y el 2027-10-19.", Assert.Single(errors).ErrorMessage);
            Assert.Equal(["From"], errors[0].MemberNames);
        }
    }

    [Theory]
    [InlineData("2015-01-01", true)]
    [InlineData("2014-12-31", false)]
    [InlineData("0001-01-05", false)]
    [InlineData("2027-10-19", true)]
    [InlineData("2027-10-20", false)]
    [InlineData("9999-12-31", false)]
    public void ToAlone_MustBeInsideTheWindow(string date, bool valid)
    {
        var errors = Validate(new MetricsQuery { To = DateOnly.Parse(date) });

        Assert.Equal(valid, errors.Count == 0);
        if (!valid)
        {
            Assert.Equal("El campo Hasta debe ser una fecha entre el 2015-01-01 y el 2027-10-19.", Assert.Single(errors).ErrorMessage);
        }
    }

    [Fact]
    public void AnExtremeEnd_IsRefusedByItself_NotAsARangeProblem()
    {
        // from=0001-01-01 & to=9999-12-31 is a "range of millions of days"; both ends are named as the problem, and nothing is computed from them.
        var errors = Validate(new MetricsQuery { From = DateOnly.MinValue, To = DateOnly.MaxValue });

        Assert.Equal(["From", "To"], errors.SelectMany(e => e.MemberNames).Order());
        Assert.DoesNotContain(errors, e => e.ErrorMessage!.Contains("rango", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWindow_FollowsTheClock()
    {
        var later = new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.NotEmpty(FixedClockValidation.Validate(new MetricsQuery { To = new DateOnly(2028, 6, 3) }, later)); // 2027-06-01 + 366 days is 2028-06-01 (leap year)
        Assert.Empty(FixedClockValidation.Validate(new MetricsQuery { To = new DateOnly(2028, 6, 1) }, later));
    }

    [Fact]
    public void WithoutAClockInTheServices_TheSystemClockIsUsed()
    {
        var results = new List<ValidationResult>();
        var query = new MetricsQuery { From = new DateOnly(9999, 12, 31) };

        Validator.TryValidateObject(query, new ValidationContext(query), results, validateAllProperties: true);

        Assert.Single(results);
    }
}
