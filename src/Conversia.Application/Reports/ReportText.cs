using System.Globalization;
using Conversia.Domain.Common;
using Conversia.Domain.Enums;

namespace Conversia.Application.Reports;

/// <summary>
/// How a report writes numbers, periods and the names of the analysis in Spanish (es-CO): the template, the renderer and the data pack use
/// these, so a number is written the same way everywhere (<c>1.234</c>, <c>23,5 %</c>) and the grounding validator reads it back.
/// </summary>
public static class ReportText
{
    private static readonly CultureInfo Spanish = SpanishCulture.Info;

    private static readonly string[] Months =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    private static readonly string[] Days = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];

    public static string Month(int month) => Months[month - 1];

    public static string DayOfWeek(int isoDay) => Days[isoDay % 7];

    /// <summary>"23,5 %": one decimal at most.</summary>
    public static string Percent(double value) => value.ToString("0.#", Spanish) + " %";

    /// <summary>"1.234".</summary>
    public static string Count(int value) => SpanishCulture.FormatWhole(value);

    /// <summary>"12,3": one decimal at most (minutes, hours, points).</summary>
    public static string Decimal(double value) => value.ToString("#,0.#", Spanish);

    /// <summary>"12,3 minutos".</summary>
    public static string Minutes(double value) => Decimal(value) + " minutos";

    /// <summary>"4,5 puntos" (always positive: the sentence says up or down).</summary>
    public static string Points(double value) => Decimal(Math.Abs(value)) + " puntos";

    public static string Date(DateOnly date) => $"{date.Day} de {Month(date.Month)} de {date.Year}";

    /// <summary>
    /// The period in words, <paramref name="to"/> exclusive: "martes 17 de noviembre de 2026", "semana del 9 al 15 de noviembre de 2026",
    /// "noviembre de 2026".
    /// </summary>
    public static string PeriodLabel(ReportFrequency frequency, DateOnly from, DateOnly to)
    {
        var until = to.AddDays(-1);
        return frequency switch
        {
            ReportFrequency.Daily => $"{Days[(int)from.DayOfWeek]} {Date(from)}",
            ReportFrequency.Monthly => $"{Month(from.Month)} de {from.Year}",
            _ when from.Year != until.Year => $"semana del {Date(from)} al {Date(until)}",
            _ when from.Month != until.Month => $"semana del {from.Day} de {Month(from.Month)} al {Date(until)}",
            _ => $"semana del {from.Day} al {Date(until)}",
        };
    }

    /// <summary>A range of days, <paramref name="to"/> exclusive: "del 2 al 8 de noviembre", "del 26 de octubre al 1 de noviembre", "del 17 de noviembre".</summary>
    public static string RangeLabel(DateOnly from, DateOnly to)
    {
        var until = to.AddDays(-1);
        if (until <= from)
        {
            return $"del {from.Day} de {Month(from.Month)}";
        }

        if (from.Year != until.Year)
        {
            return $"del {Date(from)} al {Date(until)}";
        }

        return from.Month == until.Month
            ? $"del {from.Day} al {until.Day} de {Month(until.Month)}"
            : $"del {from.Day} de {Month(from.Month)} al {until.Day} de {Month(until.Month)}";
    }

    public static string Frequency(ReportFrequency frequency) => frequency switch
    {
        ReportFrequency.Daily => "diario",
        ReportFrequency.Weekly => "semanal",
        ReportFrequency.Monthly => "mensual",
        _ => "periódico",
    };

    public static string Source(string? source) => source switch
    {
        "Ads" => "Anuncios",
        "Organic" => "Orgánico",
        _ => "Origen desconocido",
    };

    public static string LossReason(string? reason) => reason switch
    {
        "Price" => "Precio",
        "Availability" => "Disponibilidad",
        "Location" => "Ubicación",
        "Timing" => "No era el momento",
        "Competitor" => "Eligió a la competencia",
        "NoResponse" => "Dejó de responder",
        "NotInterested" => "No le interesa",
        "Other" => "Otro motivo",
        _ => "Motivo desconocido",
    };

    public static string Objection(string? objection) => objection switch
    {
        "Price" => "Precio",
        "Timing" => "Momento",
        "Location" => "Ubicación",
        "Trust" => "Confianza",
        "Pain" => "Dolor del procedimiento",
        "Other" => "Otra",
        _ => "Ninguna",
    };

    public static string ReachedStage(string? stage) => stage switch
    {
        "FirstContact" => "Primer contacto",
        "NeedsIdentified" => "Necesidad identificada",
        "PriceShared" => "Se compartió el precio",
        "ObjectionRaised" => "Planteó una objeción",
        "AppointmentProposed" => "Se propuso una cita",
        "AppointmentBooked" => "Cita agendada",
        "PaymentOrDeposit" => "Pago o abono",
        _ => "Paso desconocido",
    };

    /// <summary>The section names a person reads (the settings page, the email footer).</summary>
    public static string Section(ReportSections section) => section switch
    {
        ReportSections.Headline => "Indicadores principales",
        ReportSections.Conversion => "Conversión por sucursal, asesor y origen",
        ReportSections.DropOff => "Hasta dónde llegan las conversaciones",
        ReportSections.LossReasons => "Motivos de pérdida y objeciones",
        ReportSections.Response => "Tiempo de respuesta y conversaciones sin responder",
        ReportSections.AiCoverage => "Cobertura del análisis con IA",
        _ => section.ToString(),
    };

    /// <summary>"A, B y C".</summary>
    public static string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " y " + items[^1],
    };
}
