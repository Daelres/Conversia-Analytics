using Conversia.Application.Reports;

namespace Conversia.Application.UnitTests.Reports;

/// <summary>
/// The grounding rules (docs/REPORTS.md, "Grounding"): every number of a report's text exists in its data pack (any usual way of writing it,
/// rounded to the precision written), every name is one of the pack's, and the shape stays short.
/// </summary>
public class ReportGroundingValidatorTests
{
    private static readonly ReportDataPack Pack = ReportPacks.Sample();

    private static ReportContent With(string diagnosis, string? note = null, params string[] actions)
        => new("Resumen semanal de Láser Piloto", [new ReportIndicator("Conversión", "23,5 %", note ?? "Frente a 28 % del periodo anterior.")], diagnosis,
            actions.Length == 0 ? ["Revisar con el equipo cómo se presenta el precio."] : actions);

    private static ReportContent Plain(string diagnosis) => new("Resumen semanal", [new ReportIndicator("Valor", "dato", "Sin nota.")], diagnosis, ["Seguir así."]);

    [Theory]
    [InlineData("La conversión fue de 23,5 % y bajó 4,5 puntos.")]
    [InlineData("La conversión fue de 23.5% frente a 28,0 %.")]
    [InlineData("Aproximadamente 24 % de conversión.")]
    [InlineData("Hubo 1.234 conversaciones.")]
    [InlineData("Hubo 1234 conversaciones.")]
    [InlineData("Hubo 1,234 conversaciones.")]
    [InlineData("La mediana de respuesta fue de 12,3 minutos y la más antigua espera 3,2 horas.")]
    [InlineData("Entre el 9 y el 15 de noviembre de 2026 la sucursal Centro convirtió 28,9 % y Norte 14,3 %.")]
    [InlineData("Laura Gómez cerró 11 ventas. La mayoría se detuvo en «Se compartió el precio».")]
    [InlineData("El precio fue la objeción en 30 conversaciones; las respuestas por WhatsApp tardan.")]
    public void Accepts_the_packs_numbers_and_names_however_they_are_written(string diagnosis)
    {
        var result = ReportGroundingValidator.Validate(With(diagnosis), Pack);

        Assert.True(result.IsValid, string.Join(", ", result.Problems));
    }

    [Theory]
    [InlineData("La conversión fue de 37 %.", "number:37")]
    [InlineData("Llame al 3005550100.", "number:3005550100")]
    [InlineData("Se perdieron 41 ventas.", "number:41")]
    [InlineData("Hubo 1.235 conversaciones.", "number:1.235")]
    public void Rejects_an_invented_number(string diagnosis, string problem)
    {
        var result = ReportGroundingValidator.Validate(With(diagnosis), Pack);

        Assert.False(result.IsValid);
        Assert.Contains(problem, result.Problems);
    }

    [Theory]
    [InlineData("La sucursal Kennedy convirtió menos.")]
    [InlineData("Felicite a Pedro por su trabajo.")]
    public void Rejects_a_name_that_is_not_in_the_pack(string diagnosis)
        => Assert.Contains(ReportGroundingValidator.Validate(With(diagnosis), Pack).Problems, p => p.StartsWith("name:", StringComparison.Ordinal));

    [Fact]
    public void Rejects_the_names_the_model_says_it_used_when_they_are_not_in_the_pack_and_the_companys_other_names()
    {
        var content = With("La sucursal de siempre convirtió 23,5 %.");

        Assert.Contains("mentioned:Sur", ReportGroundingValidator.Validate(content, Pack, mentionedNames: ["Centro", "Sur"]).Problems);
        Assert.True(ReportGroundingValidator.Validate(content, Pack, mentionedNames: ["centro"]).IsValid);

        var outside = With("Kennedy no aparece en el periodo. Sur tampoco.");
        Assert.Contains("outside:Sur", ReportGroundingValidator.Validate(outside, Pack, namesOutsidePack: ["Sur"]).Problems);
    }

    [Fact]
    public void Rejects_links_addresses_and_markup()
    {
        Assert.Contains("link", ReportGroundingValidator.Validate(With("Vea https://ejemplo.test para más."), Pack).Problems);
        Assert.Contains("link", ReportGroundingValidator.Validate(With("Escriba a gerente@ejemplo.test."), Pack).Problems);
        Assert.Contains("link", ReportGroundingValidator.Validate(With("<b>Importante</b>"), Pack).Problems);
    }

    [Fact]
    public void Enforces_the_shape_three_indicators_five_actions_and_the_lengths()
    {
        var indicator = new ReportIndicator("Conversión", "23,5 %", "Bien.");
        var tooMany = new ReportContent("Resumen", [indicator, indicator, indicator, indicator], "Bien.", ["Seguir."]);
        Assert.Contains("indicators", ReportGroundingValidator.Validate(tooMany, Pack).Problems);

        var actions = new ReportContent("Resumen", [indicator], "Bien.", ["Uno.", "Dos.", "Tres.", "Cuatro.", "Cinco.", "Seis."]);
        Assert.Contains("actions", ReportGroundingValidator.Validate(actions, Pack).Problems);

        Assert.Contains("length:diagnosis", ReportGroundingValidator.Validate(With(new string('a', ReportContent.DiagnosisMaxLength + 1)), Pack).Problems);
        Assert.Contains("empty:diagnosis", ReportGroundingValidator.Validate(With(" "), Pack).Problems);
        Assert.Contains("empty:indicators", ReportGroundingValidator.Validate(new ReportContent("Resumen", [], "Bien.", ["Seguir."]), Pack).Problems);
    }

    [Fact]
    public void Property_any_number_of_the_pack_written_in_any_usual_way_is_accepted()
    {
        var random = new Random(4);
        for (var i = 0; i < 300; i++)
        {
            var pack = ReportPacks.Random(random);
            foreach (var value in ReportGroundingValidator.NumbersOf(pack))
            {
                foreach (var written in new[] { ReportText.Decimal(value), value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) })
                {
                    var result = ReportGroundingValidator.Validate(Plain($"El valor fue {written}."), pack);
                    Assert.True(result.IsValid, $"{written} of {value}: {string.Join(", ", result.Problems)}");
                }
            }
        }
    }

    [Fact]
    public void Property_a_number_far_from_every_number_of_the_pack_is_rejected()
    {
        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var pack = ReportPacks.Random(random);
            var allowed = ReportGroundingValidator.NumbersOf(pack).ToList();
            var invented = random.Next(6, 100_000);
            if (allowed.Any(v => Math.Abs(v - invented) < 1.0))
            {
                continue;
            }

            var result = ReportGroundingValidator.Validate(Plain($"Hubo {invented} casos."), pack);
            Assert.False(result.IsValid, $"{invented} passed");
        }
    }

    // ---- review: direction words, dates, rounding and units ----------------------------------------------------------------

    [Theory]
    [InlineData("La conversión subió 4,5 puntos.", "direction:subió")]
    [InlineData("La conversión mejoró frente a la semana anterior.", "direction:mejoró")]
    public void A_direction_word_must_match_the_sign_of_a_change_of_the_pack(string diagnosis, string problem)
        => Assert.Contains(problem, ReportGroundingValidator.Validate(Plain(diagnosis), Pack).Problems);

    [Fact]
    public void A_direction_word_without_any_change_in_the_pack_is_rejected_and_the_right_one_is_accepted()
    {
        Assert.True(ReportGroundingValidator.Validate(Plain("La conversión bajó 4,5 puntos."), Pack).IsValid);
        var flat = Pack with { Headline = Pack.Headline! with { ConversionChangePoints = null, PreviousConversations = Pack.Headline.Conversations } };
        Assert.Contains("direction:bajó", ReportGroundingValidator.Validate(Plain("La conversión bajó."), flat).Problems);
    }

    [Theory]
    [InlineData("Hubo 15 ventas perdidas.", "number:15")]
    [InlineData("En 2026 hubo cambios.", "number:2026")]
    public void A_day_month_or_year_of_the_pack_counts_only_inside_a_date(string diagnosis, string problem)
    {
        Assert.Contains(problem, ReportGroundingValidator.Validate(Plain(diagnosis), Pack).Problems);
        Assert.True(ReportGroundingValidator.Validate(Plain("Entre el 9 y el 15 de noviembre de 2026 no hubo alertas."), Pack).IsValid);
    }

    [Theory]
    [InlineData("El 82 % se respondió a tiempo.", "number:82")]   // 81,3 rounds to 81 or truncates to 81: never 82
    [InlineData("La conversión fue de 18 %.", "number:18")]       // 18 is a count of sales, not a percent
    [InlineData("Bajó 4,5 %.", "number:4,5")]                     // 4,5 are points, not a percent
    [InlineData("Esperan 62 minutos.", "number:62")]              // 62 is a count, not minutes
    [InlineData("Hubo 42 conversaciones.", "number:42")]          // 42 is a share, not a count
    public void A_number_must_be_of_the_kind_the_text_says_and_rounded_from_the_pack(string diagnosis, string problem)
        => Assert.Contains(problem, ReportGroundingValidator.Validate(Plain(diagnosis), Pack).Problems);

    [Fact]
    public void Property_a_near_miss_of_a_percent_of_the_pack_is_rejected()
    {
        var random = new Random(13);
        var checkedAny = 0;
        for (var i = 0; i < 3000; i++)
        {
            var pack = ReportPacks.Random(random);
            var percents = ReportGroundingValidator.TypedNumbersOf(pack).Where(n => n.Kind == GroundedKind.Percent).Select(n => n.Value).ToList();
            if (percents.Count == 0)
            {
                continue;
            }

            var target = percents[random.Next(percents.Count)];
            var missed = Math.Round(target, 1, MidpointRounding.AwayFromZero) + (random.Next(2) == 0 ? 0.6 : -0.6);
            if (missed < 0 || percents.Any(p => ReportGroundingValidator.Matches(p, (Math.Round(missed, 1), 0.1)) || ReportGroundingValidator.Matches(p, (Math.Round(missed, 0), 1))))
            {
                continue;
            }

            checkedAny++;
            var result = ReportGroundingValidator.Validate(Plain($"El dato fue {ReportText.Percent(missed)}."), pack);
            Assert.False(result.IsValid, $"{missed} passed against {string.Join(", ", percents)}");
        }

        Assert.True(checkedAny > 50);
    }
}
