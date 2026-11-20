using System.Globalization;
using Conversia.Domain.Entities;

namespace Conversia.Application.Assistant;

/// <summary>The sections of the knowledge base as the Web edits them: one item per line, columns separated by <c>|</c>.</summary>
public sealed record KnowledgeText(
    string Services, string Promotions, string OpeningHours, string Holidays, string? HowToBook, string? Deposit, string? Cancellation,
    string PaymentMethods, string Faqs, string AllowedTopics, string ForbiddenPhrases, string EscalationRules, string? AllowedLinkDomain);

/// <summary>
/// The plain-text format of the knowledge base page (docs/ASSISTANT.md, "Knowledge base"), which doubles as a simple import (paste the catalogue
/// from a spreadsheet with <c>|</c> or <c>;</c> between columns). Items of one branch only are kept as they are (the text edits the ones of every
/// branch). Errors name the section and the line, in Spanish; the domain validates the rest.
/// </summary>
public static class KnowledgeTextFormat
{
    public static readonly IReadOnlyList<(DayOfWeek Day, string Name)> Days =
    [
        (DayOfWeek.Monday, "lunes"), (DayOfWeek.Tuesday, "martes"), (DayOfWeek.Wednesday, "miércoles"), (DayOfWeek.Thursday, "jueves"),
        (DayOfWeek.Friday, "viernes"), (DayOfWeek.Saturday, "sábado"), (DayOfWeek.Sunday, "domingo"),
    ];

    public static KnowledgeText Format(AssistantKnowledge knowledge) => new(
        Lines(knowledge.Services.Where(s => s.BranchIds is not { Count: > 0 }), s =>
            $"{s.Name} | {s.Description} | {Price(s.PriceMin, s.PriceMax)} | {s.DurationMinutes} | {s.Conditions} | {(s.Active ? "activo" : "inactivo")}"),
        Lines(knowledge.Promotions.Where(p => p.BranchIds is not { Count: > 0 }), p => $"{p.Title} | {p.Description} | {p.ValidFrom:yyyy-MM-dd} | {p.ValidTo:yyyy-MM-dd}"),
        Lines(knowledge.OpeningHours.Where(h => h.BranchId is null), h => $"{DayName(h.Day)} | {Clock(h.OpensMinute)} | {Clock(h.ClosesMinute)}"),
        Lines(knowledge.Holidays.Where(h => h.BranchId is null), h => $"{h.Date:yyyy-MM-dd} | {h.Note}"),
        knowledge.BookingPolicy?.HowToBook, knowledge.BookingPolicy?.Deposit, knowledge.BookingPolicy?.Cancellation,
        string.Join('\n', knowledge.PaymentMethods),
        Lines(knowledge.Faqs, f => $"{f.Question} | {f.Answer.Replace('\n', ' ')} | {f.Topic}"),
        string.Join('\n', knowledge.AllowedTopics),
        string.Join('\n', knowledge.ForbiddenPhrases),
        string.Join('\n', knowledge.EscalationRules),
        knowledge.AllowedLinkDomain);

    /// <summary>The knowledge base the text says, keeping the branch-only items of <paramref name="previous"/>; or the errors.</summary>
    public static (AssistantKnowledge? Knowledge, IReadOnlyList<string> Errors) Parse(KnowledgeText text, AssistantKnowledge? previous = null)
    {
        var errors = new List<string>();
        var services = Rows(text.Services, "Servicios", errors, 1, 6, cells =>
        {
            var (min, max) = ParsePrice(Cell(cells, 2));
            var duration = Cell(cells, 3) is { } d ? int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ? minutes : throw new FormatException("la duración debe ser un número de minutos") : (int?)null;
            var active = Cell(cells, 5) is not { } state || !state.StartsWith("inactiv", StringComparison.OrdinalIgnoreCase);
            return new KnowledgeService(cells[0], Cell(cells, 1), min, max, duration, Cell(cells, 4), active, null);
        });
        var promotions = Rows(text.Promotions, "Promociones", errors, 4, 4, cells => new KnowledgePromotion(cells[0], Cell(cells, 1), Date(cells[2]), Date(cells[3]), null));
        var hours = Rows(text.OpeningHours, "Horarios", errors, 3, 3, cells => new KnowledgeOpeningHours(null, Day(cells[0]), Minute(cells[1]), Minute(cells[2])));
        var holidays = Rows(text.Holidays, "Festivos", errors, 1, 2, cells => new KnowledgeHoliday(Date(cells[0]), null, Cell(cells, 1)));
        var faqs = Rows(text.Faqs, "Preguntas frecuentes", errors, 2, 3, cells => new KnowledgeFaq(cells[0], cells[1], Cell(cells, 2)));
        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var knowledge = new AssistantKnowledge
        {
            Services = [.. services, .. previous?.Services.Where(s => s.BranchIds is { Count: > 0 }) ?? []],
            Promotions = [.. promotions, .. previous?.Promotions.Where(p => p.BranchIds is { Count: > 0 }) ?? []],
            OpeningHours = [.. hours, .. previous?.OpeningHours.Where(h => h.BranchId is not null) ?? []],
            Holidays = [.. holidays, .. previous?.Holidays.Where(h => h.BranchId is not null) ?? []],
            BookingPolicy = new KnowledgeBookingPolicy(Blank(text.HowToBook), Blank(text.Deposit), Blank(text.Cancellation)),
            PaymentMethods = Words(text.PaymentMethods),
            Faqs = faqs,
            AllowedTopics = Words(text.AllowedTopics),
            ForbiddenPhrases = Words(text.ForbiddenPhrases),
            EscalationRules = Words(text.EscalationRules),
            AllowedLinkDomain = Blank(text.AllowedLinkDomain),
        };
        return (knowledge, errors);
    }

    /// <summary><c>120000</c>, <c>$120.000</c>, <c>120.000 - 150.000</c> (whole pesos; dots and commas are thousands separators).</summary>
    public static (long? Min, long? Max) ParsePrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var parts = text.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2)
        {
            throw new FormatException("el precio debe ser un valor o un rango «mínimo - máximo»");
        }

        var values = parts.Select(Amount).ToArray();
        return (values[0], values.Length == 2 ? values[1] : null);
    }

    private static long Amount(string part)
    {
        var digits = part.Replace("$", string.Empty).Replace("COP", string.Empty, StringComparison.OrdinalIgnoreCase).Replace(".", string.Empty)
            .Replace(",", string.Empty).Replace(" ", string.Empty);
        return digits.Length is > 0 and <= 12 && digits.All(char.IsAsciiDigit)
            ? long.Parse(digits, CultureInfo.InvariantCulture)
            : throw new FormatException("el precio debe ser un valor en pesos, por ejemplo 120.000");
    }

    private static List<T> Rows<T>(string? text, string section, List<string> errors, int minCells, int maxCells, Func<string[], T> read)
    {
        var rows = new List<T>();
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var separator = lines[i].Contains('|') ? '|' : ';';
            var cells = lines[i].Split(separator).Select(c => c.Trim()).ToArray();
            if (cells.Length < minCells || cells.Length > maxCells || cells[0].Length == 0)
            {
                errors.Add($"{section}, línea {i + 1}: debe tener entre {minCells} y {maxCells} columnas separadas por «|» y la primera no puede quedar vacía.");
                continue;
            }

            try
            {
                rows.Add(read(cells));
            }
            catch (FormatException ex)
            {
                errors.Add($"{section}, línea {i + 1}: {ex.Message}.");
            }
        }

        return rows;
    }

    private static string? Cell(string[] cells, int index) => index < cells.Length && cells[index].Length > 0 ? cells[index] : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static List<string> Words(string? text)
        => [.. (text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    private static string Lines<T>(IEnumerable<T> items, Func<T, string> line) => string.Join('\n', items.Select(line));

    private static DateOnly Date(string text)
        => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new FormatException("la fecha debe tener la forma AAAA-MM-DD");

    private static int Minute(string text)
        => TimeOnly.TryParseExact(text, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.Hour * 60 + time.Minute
            : text is "24:00" ? 1440 : throw new FormatException("la hora debe tener la forma HH:mm");

    private static DayOfWeek Day(string text)
    {
        var normalized = AssistantSettingsNormalize(text);
        foreach (var (day, name) in Days)
        {
            if (AssistantSettingsNormalize(name) == normalized)
            {
                return day;
            }
        }

        throw new FormatException("el día debe ser lunes, martes, miércoles, jueves, viernes, sábado o domingo");
    }

    private static string AssistantSettingsNormalize(string text) => AssistantSettings.Normalize(text);

    private static string DayName(DayOfWeek day) => Days.First(d => d.Day == day).Name;

    private static string Clock(int minute) => minute == 1440 ? "24:00" : $"{minute / 60:00}:{minute % 60:00}";

    private static string Price(long? min, long? max)
        => min is null ? string.Empty : max is null ? min.Value.ToString("N0", Es) : $"{min.Value.ToString("N0", Es)} - {max.Value.ToString("N0", Es)}";

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-CO");
}
