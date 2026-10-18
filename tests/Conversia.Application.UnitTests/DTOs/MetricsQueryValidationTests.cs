using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Metrics;

namespace Conversia.Application.UnitTests.DTOs;

public class MetricsQueryValidationTests
{
    private static List<ValidationResult> Validate(MetricsQuery query)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(query, new ValidationContext(query), results, validateAllProperties: true);
        return results;
    }

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
}
