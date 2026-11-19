using Conversia.Application.Reports;

namespace Conversia.Application.UnitTests.Reports;

/// <summary>The deterministic Spanish template (docs/REPORTS.md, "Fallback"): always available, short, and grounded by construction.</summary>
public class TemplateReportWriterTests
{
    [Fact]
    public void The_sample_week_gives_three_indicators_a_diagnosis_and_concrete_actions_in_plain_spanish()
    {
        var pack = ReportPacks.Sample();

        var content = TemplateReportWriter.Write(pack);

        Assert.Contains("semana del 9 al 15 de noviembre de 2026", content.Title);
        Assert.Equal(["Conversión del 2 al 8 de noviembre", "Primera respuesta", "Conversaciones"], content.Indicators.Select(i => i.Label));
        Assert.Equal("23,5 %", content.Indicators[0].Value);
        Assert.Contains("Frente al periodo del 26 de octubre al 1 de noviembre (28 %), la conversión bajó 4,5 puntos", content.Indicators[0].Note);
        Assert.Contains("Hoy hay 6 conversaciones abiertas sin respuesta", content.Indicators[1].Note);
        Assert.Contains("Norte", content.Diagnosis);
        Assert.Contains("Se compartió el precio", content.Diagnosis);
        Assert.Contains("el paso donde más se detuvieron fue «Se compartió el precio» (21, el 42 %)", content.Diagnosis); // 42 % is not a majority
        Assert.Contains(content.Actions, a => a.Contains("Responder hoy las 6 conversaciones abiertas", StringComparison.Ordinal));
        Assert.InRange(content.Actions.Count, 1, ReportContent.MaxActions);
        Assert.NotNull(content.CoverageNote);
        var result = ReportGroundingValidator.Validate(content, pack);
        Assert.True(result.IsValid, string.Join(", ", result.Problems));
    }

    [Fact]
    public void A_window_with_too_few_decided_conversations_states_no_rate_and_no_change()
    {
        var sample = ReportPacks.Sample();
        var pack = sample with
        {
            Headline = sample.Headline! with
            {
                Conversion = sample.Headline.Conversion with { Decided = 2, Won = 1, Rate = null },
                ConversionChangePoints = null,
            },
        };

        var content = TemplateReportWriter.Write(pack);

        Assert.Equal("Sin datos suficientes", content.Indicators[0].Value);
        Assert.Contains("Hubo 2 conversaciones decididas; para medir la conversión se necesitan al menos 20", content.Indicators[0].Note);
        Assert.DoesNotContain("%", content.Indicators[0].Note);
        Assert.DoesNotContain("bajó", content.Indicators[0].Note);
        Assert.True(ReportGroundingValidator.Validate(content, pack).IsValid);
    }

    [Theory]
    [InlineData(30.0, "el paso donde más se detuvieron fue «Se compartió el precio»")]
    [InlineData(50.0, "el paso donde más se detuvieron fue «Se compartió el precio»")]
    [InlineData(50.1, "la mayoría se detuvo en «Se compartió el precio»")]
    public void The_majority_is_said_only_above_half(double share, string expected)
    {
        var sample = ReportPacks.Sample();
        var pack = sample with { DropOff = sample.DropOff! with { MainStageSharePercent = share } };

        var content = TemplateReportWriter.Write(pack);

        Assert.Contains(expected, content.Diagnosis);
        Assert.True(ReportGroundingValidator.Validate(content, pack).IsValid);
    }

    [Fact]
    public void Only_the_chosen_sections_are_written()
    {
        var sample = ReportPacks.Sample();
        var pack = sample with { Headline = null, Conversion = null, DropOff = null, Losses = null, Coverage = null };

        var content = TemplateReportWriter.Write(pack);

        Assert.Equal("Primera respuesta", Assert.Single(content.Indicators).Label);
        Assert.DoesNotContain("precio", content.Diagnosis, StringComparison.OrdinalIgnoreCase);
        Assert.Null(content.CoverageNote);
    }

    [Fact]
    public void A_period_without_conversations_is_still_a_short_valid_report()
    {
        var sample = ReportPacks.Sample();
        var pack = sample with
        {
            Headline = sample.Headline! with
            {
                Conversations = 0, Won = 0, PreviousConversations = 0,
                Conversion = sample.Headline.Conversion with { Decided = 0, Won = 0, Rate = null },
                PreviousConversion = sample.Headline.PreviousConversion with { Decided = 0, Won = 0, Rate = null },
                ConversionChangePoints = null,
            },
            Conversion = new ReportConversion(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8), "del 2 al 8 de noviembre", null, 20, [], [], []),
            DropOff = new ReportDropOff(0, 0, null, null, null, 0),
            Losses = new ReportLosses(0, 0, null, [], []),
            Response = new ReportResponseTimes(30, null, null, 0, 0, 0, null, []),
            Coverage = new ReportCoverage(false, 0, 0, null, null),
        };

        var content = TemplateReportWriter.Write(pack);

        Assert.Contains("no hubo conversaciones", content.Diagnosis, StringComparison.OrdinalIgnoreCase);
        Assert.True(ReportGroundingValidator.Validate(content, pack).IsValid);
    }

    [Fact]
    public void Property_whatever_the_pack_the_template_is_grounded_and_within_the_shape()
    {
        var random = new Random(11);
        for (var i = 0; i < 2000; i++)
        {
            var pack = ReportPacks.Random(random);

            var content = TemplateReportWriter.Write(pack);

            var result = ReportGroundingValidator.Validate(content, pack);
            Assert.True(result.IsValid, $"pack {i}: {string.Join(", ", result.Problems)}\n{content.ToJson()}\n{pack.ToJson()}");
        }
    }
}
