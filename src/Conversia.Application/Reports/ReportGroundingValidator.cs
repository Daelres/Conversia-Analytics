using System.Globalization;
using System.Text.RegularExpressions;

namespace Conversia.Application.Reports;

/// <summary>What the validator found; empty = the text is grounded. Codes only (<c>number:37</c>, <c>name:Kennedy</c>), never the text.</summary>
public sealed record GroundingResult(IReadOnlyList<string> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Checks a report's text against the data pack it was written from (docs/REPORTS.md, "Grounding"). A text the model wrote is used only when
/// this finds nothing:
/// <list type="bullet">
/// <item><b>Numbers</b>: every number of the text equals a number of the pack (its absolute value, or a day, month or year of its dates) at the
/// precision written: <c>23,5 %</c>, <c>23.5%</c> and <c>24 %</c> all match 23.5 (within one unit of the last digit written), <c>1.234</c> and
/// <c>1,234</c> match 1234 (Spanish or English separators), <c>37</c> matches nothing.</item>
/// <item><b>Names</b>: a capitalized word in the middle of a sentence must be part of a name of the pack (company, branches, advisors, labels)
/// or of a short list of product words; the names the model declares it used must be the pack's; and no other name of the company (a branch or
/// advisor outside the report's scope) may appear.</item>
/// <item><b>Shape</b>: one to three indicators, one to five actions, the length limits of <see cref="ReportContent"/>, no links, addresses or markup.</item>
/// </list>
/// </summary>
public enum GroundedKind
{
    Count,
    Percent,
    Points,
    Minutes,
    Hours,
    Days,
    Other,
    Date,
}

public sealed record GroundedNumber(double Value, GroundedKind Kind);

public static partial class ReportGroundingValidator
{
    /// <summary>
    /// Every number of the pack with its kind, read from the pack's JSON (a field added later is covered): the property name says the kind
    /// (<c>Rate</c>/<c>Percent</c>, <c>Points</c>, <c>Minutes</c>, <c>Hours</c>, <c>Days</c>), a whole number is a count, and the day, month and
    /// year of a date are <see cref="GroundedKind.Date"/> (accepted only inside a written date).
    /// </summary>
    public static IReadOnlyList<GroundedNumber> TypedNumbersOf(ReportDataPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var values = new List<GroundedNumber>();
        Collect(System.Text.Json.JsonSerializer.SerializeToElement(pack, ReportDataPack.JsonOptions), null, values);
        return [.. values.Distinct()];
    }

    /// <summary>Capitalized words a report may use that are not names of the pack.</summary>
    private static readonly HashSet<string> ProductWords = new(StringComparer.Ordinal) { "WhatsApp", "IA", "Conversia", "Instagram", "Facebook", "Meta", "Google", "SLA" };

    public static GroundingResult Validate(
        ReportContent content, ReportDataPack pack, IReadOnlyCollection<string>? mentionedNames = null, IReadOnlyCollection<string>? namesOutsidePack = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(pack);
        var problems = new List<string>();
        CheckShape(content, problems);

        var numbers = TypedNumbersOf(pack);
        // The changes a direction word may describe: the conversion against the previous period, and the volume of conversations.
        var changes = (Rate: new List<double>(), Volume: new List<double>());
        if (pack.Headline is { } headline)
        {
            if (headline.ConversionChangePoints is { } points)
            {
                changes.Rate.Add(points);
            }

            changes.Volume.Add(headline.Conversations - headline.PreviousConversations);
        }

        var packNames = pack.Names().Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var nameWords = new HashSet<string>(packNames.SelectMany(n => WordPattern().Matches(n).Select(m => m.Value)), StringComparer.Ordinal);
        foreach (var text in content.Texts().Where(t => t is not null))
        {
            if (text.Contains("http", StringComparison.OrdinalIgnoreCase) || text.Contains("www.", StringComparison.OrdinalIgnoreCase)
                || text.Contains('@') || text.Contains('<') || text.Contains('>'))
            {
                Add(problems, "link");
            }

            foreach (Match match in NumberPattern().Matches(text))
            {
                var unit = UnitAfter(text, match.Index + match.Length);
                var inDate = unit is null && InDate(text, match.Index, match.Index + match.Length);
                var candidates = numbers.Where(n => unit is { } kind ? n.Kind == kind : n.Kind != GroundedKind.Date || inDate).Select(n => n.Value).ToList();
                if (!Readings(match.Value).Any(r => candidates.Any(v => Matches(v, r))))
                {
                    Add(problems, "number:" + match.Value);
                }
            }

            foreach (var sentence in text.Split(['.', '!', '?', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                // The change a sentence speaks of: the conversion (rate) or the conversations (volume); either when it names neither.
                var aboutRate = sentence.Contains("conversión", StringComparison.OrdinalIgnoreCase) || sentence.Contains("tasa", StringComparison.OrdinalIgnoreCase);
                var aboutVolume = sentence.Contains("conversaciones", StringComparison.OrdinalIgnoreCase);
                var related = aboutRate == aboutVolume ? [.. changes.Rate, .. changes.Volume] : aboutRate ? changes.Rate : changes.Volume;
                foreach (Match match in DirectionWords().Matches(sentence))
                {
                    var up = UpWords.Contains(match.Value.ToLowerInvariant());
                    if (!related.Any(c => up ? c > 0 : c < 0))
                    {
                        Add(problems, "direction:" + match.Value);
                    }
                }
            }

            foreach (Match match in CapitalizedPattern().Matches(text))
            {
                if (!StartsSentence(text, match.Index) && !nameWords.Contains(match.Value) && !ProductWords.Contains(match.Value))
                {
                    Add(problems, "name:" + match.Value);
                }
            }

            foreach (var outside in namesOutsidePack ?? [])
            {
                if (!string.IsNullOrWhiteSpace(outside) && !packNames.Contains(outside, StringComparer.Ordinal)
                    && Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(outside.Trim())}(?![\p{{L}}\p{{M}}])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                {
                    Add(problems, "outside:" + outside.Trim());
                }
            }
        }

        foreach (var name in mentionedNames ?? [])
        {
            if (!packNames.Contains(name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                Add(problems, "mentioned:" + name);
            }
        }

        return new GroundingResult(problems);
    }

    /// <summary>Every number of the pack that a text may write anywhere (its dates' parts only inside a date).</summary>
    public static IEnumerable<double> NumbersOf(ReportDataPack pack)
        => TypedNumbersOf(pack).Where(n => n.Kind != GroundedKind.Date).Select(n => n.Value).Distinct();

    private static void Collect(System.Text.Json.JsonElement element, string? property, List<GroundedNumber> values)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var child in element.EnumerateObject())
                {
                    Collect(child.Value, child.Name, values);
                }

                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, property, values);
                }

                break;
            case System.Text.Json.JsonValueKind.Number:
                var value = Math.Abs(element.GetDouble());
                values.Add(new GroundedNumber(value, KindOf(property, value)));
                break;
            case System.Text.Json.JsonValueKind.String when DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                values.Add(new GroundedNumber(date.Day, GroundedKind.Date));
                values.Add(new GroundedNumber(date.Month, GroundedKind.Date));
                values.Add(new GroundedNumber(date.Year, GroundedKind.Date));
                break;
        }
    }

    private static GroundedKind KindOf(string? property, double value)
    {
        var name = property ?? string.Empty;
        if (name.Contains("Percent", StringComparison.OrdinalIgnoreCase) || name.Contains("Rate", StringComparison.OrdinalIgnoreCase))
        {
            return GroundedKind.Percent;
        }

        if (name.Contains("Points", StringComparison.OrdinalIgnoreCase))
        {
            return GroundedKind.Points;
        }

        if (name.Contains("Minutes", StringComparison.OrdinalIgnoreCase))
        {
            return GroundedKind.Minutes;
        }

        if (name.Contains("Hours", StringComparison.OrdinalIgnoreCase))
        {
            return GroundedKind.Hours;
        }

        if (name.Contains("Days", StringComparison.OrdinalIgnoreCase))
        {
            return GroundedKind.Days;
        }

        return value == Math.Floor(value) ? GroundedKind.Count : GroundedKind.Other;
    }

    // What the words right after a number say it is; null = nothing (any kind but a date).
    private static GroundedKind? UnitAfter(string text, int end)
    {
        var after = text[end..];
        if (PercentAfter().IsMatch(after)) return GroundedKind.Percent;
        if (PointsAfter().IsMatch(after)) return GroundedKind.Points;
        if (MinutesAfter().IsMatch(after)) return GroundedKind.Minutes;
        if (HoursAfter().IsMatch(after)) return GroundedKind.Hours;
        if (DaysAfter().IsMatch(after)) return GroundedKind.Days;
        return CountAfter().IsMatch(after) ? GroundedKind.Count : null;
    }

    // A day ("9 de noviembre", "9 al 15 de noviembre") or a year ("noviembre de 2026") written as part of a date.
    private static bool InDate(string text, int start, int end)
        => DayContext().IsMatch(text[end..]) || YearContext().IsMatch(text[..start]);

    // Written with d decimals, w matches v when v rounds (half away from zero) or truncates to w at that precision: 23 and 24 match 23,5; 23 never matches 22,1.
    internal static bool Matches(double v, (double Value, double Unit) written)
    {
        var decimals = (int)Math.Round(-Math.Log10(written.Unit));
        var exact = (decimal)v;
        var w = (decimal)written.Value;
        return Math.Round(exact, decimals, MidpointRounding.AwayFromZero) == w || decimal.Truncate(exact * Pow10(decimals)) / Pow10(decimals) == w;
    }

    private static decimal Pow10(int n)
    {
        var result = 1m;
        for (var i = 0; i < n; i++)
        {
            result *= 10m;
        }

        return result;
    }

    /// <summary>The ways a written number can be read, each with the unit of its last digit: "1.234" is 1234 (Spanish) or 1.234 (English).</summary>
    internal static IEnumerable<(double Value, double Unit)> Readings(string written)
    {
        var dots = written.Count(c => c == '.');
        var commas = written.Count(c => c == ',');
        if (dots == 0 && commas == 0)
        {
            yield return (double.Parse(written, CultureInfo.InvariantCulture), 1);
            yield break;
        }

        // Spanish: '.' groups thousands, ',' is the decimal mark. English: the other way round.
        foreach (var (group, mark) in new[] { ('.', ','), (',', '.') })
        {
            if (Parse(written, group, mark) is { } reading)
            {
                yield return reading;
            }
        }
    }

    private static (double, double)? Parse(string written, char group, char mark)
    {
        var parts = written.Split(mark);
        if (parts.Length > 2)
        {
            return null;
        }

        var whole = parts[0].Split(group);
        if (whole.Length > 1 && (whole[0].Length is 0 or > 3 || whole.Skip(1).Any(g => g.Length != 3)))
        {
            return null;
        }

        var digits = string.Concat(whole);
        var decimals = parts.Length == 2 ? parts[1] : string.Empty;
        if (decimals.Contains(group))
        {
            return null;
        }

        var value = double.Parse(decimals.Length == 0 ? digits : digits + "." + decimals, CultureInfo.InvariantCulture);
        return (value, Math.Pow(10, -decimals.Length));
    }

    // A word at the start of the text, or after the end of a sentence, a colon, a line break, an opening quote or bracket, or a bullet.
    private static bool StartsSentence(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && text[i] is ' ' or ' ' or '\t')
        {
            i--;
        }

        return i < 0 || text[i] is '.' or '!' or '?' or ':' or ';' or '\n' or '\r' or '«' or '"' or '“' or '(' or '¿' or '¡' or '-' or '—' or '•' or '\'';
    }

    private static void CheckShape(ReportContent content, List<string> problems)
    {
        Check(content.Title, ReportContent.TitleMaxLength, "title", problems);
        Check(content.Diagnosis, ReportContent.DiagnosisMaxLength, "diagnosis", problems);
        if (content.Indicators is null || content.Indicators.Count == 0)
        {
            Add(problems, "empty:indicators");
        }
        else if (content.Indicators.Count > ReportContent.MaxIndicators)
        {
            Add(problems, "indicators");
        }

        foreach (var indicator in content.Indicators ?? [])
        {
            Check(indicator?.Label, ReportContent.LabelMaxLength, "label", problems);
            Check(indicator?.Value, ReportContent.ValueMaxLength, "value", problems);
            Check(indicator?.Note, ReportContent.NoteMaxLength, "note", problems);
        }

        if (content.Actions is null || content.Actions.Count == 0)
        {
            Add(problems, "empty:actions");
        }
        else if (content.Actions.Count > ReportContent.MaxActions)
        {
            Add(problems, "actions");
        }

        foreach (var action in content.Actions ?? [])
        {
            Check(action, ReportContent.ActionMaxLength, "action", problems);
        }

        if (content.CoverageNote is { Length: > ReportContent.CoverageNoteMaxLength })
        {
            Add(problems, "length:coverage_note");
        }

        if (content.Indicators is not null && content.Actions is not null && content.Texts().Sum(t => t?.Length ?? 0) > ReportContent.TotalMaxLength)
        {
            Add(problems, "length:total");
        }
    }

    private static void Check(string? text, int maxLength, string field, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Add(problems, "empty:" + field);
        }
        else if (text.Length > maxLength)
        {
            Add(problems, "length:" + field);
        }
    }

    private static void Add(List<string> problems, string problem)
    {
        if (!problems.Contains(problem))
        {
            problems.Add(problem);
        }
    }

    private static readonly HashSet<string> UpWords = ["subió", "subieron", "aumentó", "aumentaron", "mejoró", "mejoraron", "creció", "crecieron"];

    private const string Months = "enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|octubre|noviembre|diciembre";

    [GeneratedRegex(@"(?<![\p{L}])(subi[óo]|subieron|aument[óo]|aumentaron|mejor[óo]|mejoraron|creci[óo]|crecieron|baj[óo]|bajaron|disminuy[óo]|disminuyeron|empeor[óo]|empeoraron|cay[óo]|cayeron)(?![\p{L}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex DirectionWords();

    [GeneratedRegex(@"^\s*%", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PercentAfter();

    [GeneratedRegex(@"^\s*puntos?(?![\p{L}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex PointsAfter();

    [GeneratedRegex(@"^\s*(minutos?|min)(?![\p{L}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex MinutesAfter();

    [GeneratedRegex(@"^\s*horas?(?![\p{L}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HoursAfter();

    [GeneratedRegex(@"^\s*d[ií]as?(?![\p{L}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex DaysAfter();

    [GeneratedRegex(@"^\s*(conversaciones|conversación|ventas|venta|personas|clientes|mensajes)(?![\p{L}])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex CountAfter();

    [GeneratedRegex(@"^\s+(al\s+\d{1,2}\s+)?de\s+(" + Months + ")", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex DayContext();

    [GeneratedRegex(@"(" + Months + @")\s+de\s+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex YearContext();

    [GeneratedRegex(@"\d+(?:[.,]\d+)*", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"(?<![\p{L}\p{M}\d])\p{Lu}[\p{L}\p{M}]*", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CapitalizedPattern();

    [GeneratedRegex(@"[\p{L}\p{M}]+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex WordPattern();
}
