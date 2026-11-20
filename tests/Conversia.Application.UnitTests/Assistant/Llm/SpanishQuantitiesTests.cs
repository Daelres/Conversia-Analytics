using Conversia.Application.Assistant.Llm;

namespace Conversia.Application.UnitTests.Assistant.Llm;

/// <summary>How the reply checks read prices, percentages, durations, hours and dates written the Colombian way.</summary>
public class SpanishQuantitiesTests
{
    [Theory]
    [InlineData("La sesión vale $80.000", 80_000)]
    [InlineData("La sesión vale $ 80.000 pesos", 80_000)]
    [InlineData("queda en 80 mil", 80_000)]
    [InlineData("son 80.000 pesos", 80_000)]
    [InlineData("el paquete sale en 1,2 millones", 1_200_000)]
    [InlineData("el paquete sale en $1.200.000", 1_200_000)]
    [InlineData("cuesta 80000 COP", 80_000)]
    [InlineData("vale 180.000", 180_000)]
    public void Prices_AreRead_InEveryLocalForm(string text, double value)
    {
        var price = Assert.Single(SpanishQuantities.Read(text), q => q.Kind == QuantityKind.Price);
        Assert.Equal(value, price.Value);
    }

    [Theory]
    [InlineData("tienes un 20% de descuento", QuantityKind.Percent, 20)]
    [InlineData("un 20 por ciento menos", QuantityKind.Percent, 20)]
    [InlineData("la sesión dura 20 minutos", QuantityKind.Minutes, 20)]
    [InlineData("dura 20 min", QuantityKind.Minutes, 20)]
    [InlineData("toma 2 horas", QuantityKind.Hours, 2)]
    [InlineData("se recomiendan 6 sesiones", QuantityKind.Count, 6)]
    public void OtherKinds_AreRead_ByTheWordsAfterTheNumber(string text, QuantityKind kind, double value)
    {
        var quantity = Assert.Single(SpanishQuantities.Read(text));
        Assert.Equal(kind, quantity.Kind);
        Assert.Equal(value, quantity.Value);
    }

    [Theory]
    [InlineData("abrimos a las 8", 8 * 60, 20 * 60)]
    [InlineData("abrimos a las 8:30", 8 * 60 + 30, 20 * 60 + 30)]
    [InlineData("cerramos a las 6 pm", 18 * 60, null)]
    [InlineData("cerramos a las 6 p. m.", 18 * 60, null)]
    [InlineData("hasta las 18:00", 18 * 60, null)]
    [InlineData("desde las 9 am", 9 * 60, null)]
    public void ClockTimes_AreRead_WithTheirAfternoonReading(string text, int minutes, int? alternative)
    {
        var clock = Assert.Single(SpanishQuantities.Read(text), q => q.Kind == QuantityKind.Clock);
        Assert.Equal(minutes, clock.Value);
        Assert.Equal(alternative, clock.Alternative is { } a ? (int)a : null);
    }

    [Theory]
    [InlineData("válida hasta el 30 de noviembre", 11, 30)]
    [InlineData("del 1 al 30 de noviembre", 11, 30)]
    [InlineData("el 8 de diciembre no abrimos", 12, 8)]
    public void Dates_AreRead_AsMonthAndDay(string text, int month, int day)
        => Assert.Contains(SpanishQuantities.Read(text), q => q.Kind == QuantityKind.Date && q.Value == month * 100 + day);

    [Fact]
    public void AWordWithoutDigits_IsNotAQuantity()
        => Assert.Empty(SpanishQuantities.Read("mil gracias por escribirnos"));
}

/// <summary>Review round: numbers written in words, slang units, half hours, clock words and Colombian thousands with a comma.</summary>
public class SpanishQuantitiesReviewTests
{
    [Theory]
    [InlineData("la sesión vale cincuenta mil", 50_000)]
    [InlineData("son ochenta mil pesos", 80_000)]
    [InlineData("el paquete queda en medio millón", 500_000)]
    [InlineData("el paquete queda en un millón doscientos", 1_200_000)]
    [InlineData("el paquete queda en un millón doscientos mil", 1_200_000)]
    [InlineData("te sale en ciento ochenta mil", 180_000)]
    [InlineData("te sale en 50k", 50_000)]
    [InlineData("te sale en 50 lucas", 50_000)]
    [InlineData("vale $80,000", 80_000)]
    [InlineData("vale 80,000 pesos", 80_000)]
    [InlineData("el paquete vale 1,200,000", 1_200_000)]
    public void PricesInWordsAndSlang_AreRead(string text, double value)
        => Assert.Contains(SpanishQuantities.Read(text), q => q.Kind == QuantityKind.Price && q.Value == value);

    [Fact]
    public void ACountNextToAPriceWord_IsAPrice_ReadInThousandsToo()
    {
        var price = Assert.Single(SpanishQuantities.Read("la sesión cuesta 85"));
        Assert.Equal(QuantityKind.Price, price.Kind);
        Assert.Equal(85_000, price.Alternative);
    }

    [Theory]
    [InlineData("dura 1 hora y media", QuantityKind.Minutes, 90)]
    [InlineData("dura una hora y media", QuantityKind.Minutes, 90)]
    [InlineData("dura media hora", QuantityKind.Minutes, 30)]
    [InlineData("dura hora y media", QuantityKind.Minutes, 90)]
    [InlineData("toma dos horas", QuantityKind.Minutes, 120)]
    [InlineData("tienes veinte por ciento menos", QuantityKind.Percent, 20)]
    public void DurationsAndPercentsInWords_AreRead(string text, QuantityKind kind, double value)
    {
        var q = SpanishQuantities.Read(text).Single(x => x.Kind is QuantityKind.Minutes or QuantityKind.Hours or QuantityKind.Percent);
        var minutes = q.Kind == QuantityKind.Hours ? q.Value * 60 : q.Value;
        Assert.Equal(value, kind == QuantityKind.Percent ? q.Value : minutes);
    }

    [Theory]
    [InlineData("te esperamos a las tres", 3 * 60, 15 * 60)]
    [InlineData("abrimos a las ocho y media", 8 * 60 + 30, 20 * 60 + 30)]
    [InlineData("a las tres de la tarde", 15 * 60, null)]
    [InlineData("a las 3 de la tarde", 15 * 60, null)]
    [InlineData("a las nueve de la mañana", 9 * 60, null)]
    public void ClockWords_AreRead(string text, int minutes, int? alternative)
    {
        var clock = Assert.Single(SpanishQuantities.Read(text), q => q.Kind == QuantityKind.Clock);
        Assert.Equal(minutes, clock.Value);
        Assert.Equal(alternative, clock.Alternative is { } a ? (int)a : null);
    }

    [Theory]
    [InlineData("mañana abrimos", 2026, 11, 20)]
    [InlineData("pasado mañana abrimos", 2026, 11, 21)]
    [InlineData("hoy abrimos", 2026, 11, 19)]
    [InlineData("el sábado abrimos", 2026, 11, 21)]
    [InlineData("el lunes abrimos", 2026, 11, 23)]
    [InlineData("el jueves abrimos", 2026, 11, 19)]
    public void RelativeDays_AreConcreteDates(string text, int year, int month, int day)
    {
        var today = new DateOnly(2026, 11, 19); // a Thursday
        Assert.Contains(SpanishQuantities.Read(text, today), q => q.Kind == QuantityKind.Date && q.Value == month * 100 + day && q.Day == new DateOnly(year, month, day));
    }

    [Theory]
    [InlineData("una o dos sesiones")]
    [InlineData("en tres días te escribimos")]
    public void SmallNumbersInWordsWithoutAUnit_AreNotChecked(string text)
        => Assert.DoesNotContain(SpanishQuantities.Read(text), q => q.Kind is QuantityKind.Price or QuantityKind.Percent);
}
