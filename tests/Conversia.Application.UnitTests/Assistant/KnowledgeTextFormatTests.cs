using Conversia.Application.Assistant;
using Conversia.Domain.Entities;

namespace Conversia.Application.UnitTests.Assistant;

public class KnowledgeTextFormatTests
{
    private static KnowledgeText Text(string services = "", string hours = "", string faqs = "", string promotions = "", string holidays = "")
        => new(services, promotions, hours, holidays, "Por WhatsApp", null, null, "Nequi\nTarjeta", faqs, "depilación", "", "", "reservas.ejemplo.com");

    [Theory]
    [InlineData("120000", 120_000L, null)]
    [InlineData("$120.000", 120_000L, null)]
    [InlineData("120.000 - 150.000", 120_000L, 150_000L)]
    [InlineData("80,000 COP", 80_000L, null)]
    public void Prices_are_read_in_whole_pesos(string text, long min, long? max)
        => Assert.Equal((min, max), KnowledgeTextFormat.ParsePrice(text));

    [Fact]
    public void A_catalogue_pasted_with_semicolons_is_read()
    {
        var (knowledge, errors) = KnowledgeTextFormat.Parse(Text(services: "Axilas;Sesión corta;60.000;20;;activo\nPiernas | | 150.000 - 180.000 | 45 | Requiere valoración | inactivo"));
        Assert.Empty(errors);
        Assert.Equal(2, knowledge!.Services.Count);
        Assert.Equal((60_000L, (long?)null, 20, true), (knowledge.Services[0].PriceMin!.Value, knowledge.Services[0].PriceMax, knowledge.Services[0].DurationMinutes!.Value, knowledge.Services[0].Active));
        Assert.False(knowledge.Services[1].Active);
        Assert.Equal(["Nequi", "Tarjeta"], knowledge.PaymentMethods);
    }

    [Fact]
    public void Errors_name_the_section_and_the_line()
    {
        var (knowledge, errors) = KnowledgeTextFormat.Parse(Text(services: "Axilas | | mucho", hours: "\nfunes | 08:00 | 18:00", faqs: "solo pregunta"));
        Assert.Null(knowledge);
        Assert.Contains(errors, e => e.StartsWith("Servicios, línea 1", StringComparison.Ordinal) && e.Contains("precio"));
        Assert.Contains(errors, e => e.StartsWith("Horarios, línea 2", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Preguntas frecuentes, línea 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Format_and_parse_round_trip_and_branch_items_are_kept()
    {
        var branch = Guid.NewGuid();
        var original = new AssistantKnowledge
        {
            Services = [new KnowledgeService("Axilas", "Corta", 60_000, null, 20, null, true, null), new KnowledgeService("Solo norte", null, 10, null, null, null, true, [branch])],
            OpeningHours = [new KnowledgeOpeningHours(null, DayOfWeek.Saturday, 480, 780)],
            Holidays = [new KnowledgeHoliday(new DateOnly(2026, 12, 8), null, "Festivo")],
            Promotions = [new KnowledgePromotion("2x1", "En diciembre", new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), null)],
            Faqs = [new KnowledgeFaq("¿Duele?", "Poco", "procedimiento")],
            AllowedLinkDomain = "reservas.ejemplo.com",
        };

        var text = KnowledgeTextFormat.Format(original);
        Assert.Equal("sábado | 08:00 | 13:00", text.OpeningHours);
        var (parsed, errors) = KnowledgeTextFormat.Parse(text, original);

        Assert.Empty(errors);
        Assert.Equal(["Axilas", "Solo norte"], parsed!.Services.Select(s => s.Name));
        Assert.Equal(original.OpeningHours, parsed.OpeningHours);
        Assert.Equal(original.Holidays, parsed.Holidays);
        Assert.Equal(original.Faqs, parsed.Faqs);
        Assert.Equal(60_000, parsed.Services[0].PriceMin);
    }
}
