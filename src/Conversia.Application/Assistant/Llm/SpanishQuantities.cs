using System.Globalization;
using System.Text.RegularExpressions;

namespace Conversia.Application.Assistant.Llm;

/// <summary>What a number written in a reply or in the knowledge base stands for.</summary>
public enum QuantityKind
{
    /// <summary>Colombian pesos: <c>$80.000</c>, <c>80 mil</c>, <c>80.000 pesos</c>, <c>1,2 millones</c>, or any whole amount of 1,000 or more.</summary>
    Price,
    Percent,
    Minutes,
    Hours,

    /// <summary>A time of day, in minutes after midnight (<see cref="Quantity.Alternative"/>: the afternoon reading of an hour without am/pm).</summary>
    Clock,

    /// <summary>A day of a month, as <c>month x 100 + day</c>.</summary>
    Date,

    /// <summary>Any other number (sessions, weeks, people).</summary>
    Count,
}

/// <param name="Written">The number as written (for the problem code).</param>
public readonly record struct Quantity(QuantityKind Kind, double Value, double? Alternative, string Written)
{
    /// <summary>For a date read from a relative word (hoy, mañana, a weekday): the concrete day.</summary>
    public DateOnly? Day { get; init; }
}

/// <summary>
/// Reads the quantities of a Spanish (Colombia) text for the grounding check of the assistant's replies (docs/ASSISTANT.md, "Reply checks"):
/// the kind comes from the words around the number, so <c>20 minutos</c> is never matched against a price of 20. Separators are read the
/// Colombian way first (<c>.</c> groups thousands, <c>,</c> is the decimal mark) and the English way second. Linear patterns only.
/// </summary>
public static partial class SpanishQuantities
{
    private static readonly string[] Months = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    /// <summary>
    /// The quantities of <paramref name="text"/>. Numbers written in words are read when they are 100 or more or carry a unit ("ochenta mil
    /// pesos", "veinte por ciento", "dos horas", "a las tres"); slang units ("50k", "50 lucas", "2 palos") and half hours ("1 hora y media",
    /// "media hora") are read too. With <paramref name="today"/>, "hoy", "mañana", "pasado mañana" and weekday names are dates.
    /// </summary>
    public static IReadOnlyList<Quantity> Read(string text, DateOnly? today = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lower = Normalize(text.ToLowerInvariant());
        var found = new List<Quantity>();
        if (today is { } day)
        {
            found.AddRange(RelativeDays(lower, day));
        }

        var skipUntil = -1;
        foreach (Match match in NumberPattern().Matches(lower))
        {
            if (match.Index < skipUntil)
            {
                continue;
            }

            var written = match.Value.TrimEnd('.', ',');
            var end = match.Index + written.Length;
            var before = lower[..match.Index];
            var after = lower[end..];

            // "2x1", "3 x 2": an offer's name, not two quantities.
            var times = TimesAfter().Match(after);
            if (times.Success)
            {
                skipUntil = end + times.Length;
                continue;
            }

            // 8:30, 18:00 (and an optional am/pm).
            var clock = ClockAfter().Match(after);
            if (clock.Success && int.TryParse(written, out var hour) && hour <= 24)
            {
                var minute = int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture);
                found.Add(ClockOf(hour, minute, clock.Groups["ampm"].Value, written + ":" + clock.Groups["m"].Value));
                skipUntil = end + clock.Length;
                continue;
            }

            var value = Value(written);
            if (value is null)
            {
                continue;
            }

            var number = value.Value;
            if (CurrencyBefore().IsMatch(before) || Multiplier(after) is not null || PesosAfter().IsMatch(after))
            {
                var multiplier = Multiplier(after) ?? 1;
                found.Add(new Quantity(QuantityKind.Price, Math.Round(number * multiplier), null, written));
                continue;
            }

            if (PercentAfter().IsMatch(after))
            {
                found.Add(new Quantity(QuantityKind.Percent, number, null, written));
                continue;
            }

            var ampm = AmPmAfter().Match(after);
            if (ampm.Success && number is >= 0 and <= 24 && number == Math.Floor(number))
            {
                found.Add(ClockOf((int)number, 0, ampm.Groups["ampm"].Value, written));
                continue;
            }

            if (MinutesAfter().IsMatch(after))
            {
                found.Add(new Quantity(QuantityKind.Minutes, number, null, written));
                continue;
            }

            if (HoursAfter().IsMatch(after))
            {
                found.Add(new Quantity(QuantityKind.Hours, number, null, written));
                continue;
            }

            var date = DateAfter().Match(after);
            if (date.Success && number is >= 1 and <= 31)
            {
                found.Add(new Quantity(QuantityKind.Date, (Array.IndexOf(Months, date.Groups["month"].Value) + 1) * 100 + number, null, written));
                continue;
            }

            if (ClockBefore().IsMatch(before) && number is >= 0 and <= 24 && number == Math.Floor(number) && !NounFollows(after))
            {
                found.Add(ClockOf((int)number, 0, string.Empty, written));
                continue;
            }

            // "cuesta 85", "queda en 80": a price said in thousands.
            if (number < 1000 && PriceWordBefore().IsMatch(before))
            {
                found.Add(new Quantity(QuantityKind.Price, number, number * 1000, written));
                continue;
            }

            found.Add(new Quantity(number >= 1000 && number == Math.Floor(number) ? QuantityKind.Price : QuantityKind.Count, number, null, written));
        }

        return found;
    }

    private static Quantity ClockOf(int hour, int minute, string ampm, string written)
    {
        var pm = ampm.StartsWith('p');
        var am = ampm.StartsWith('a');
        if (pm && hour < 12)
        {
            hour += 12;
        }
        else if (am && hour == 12)
        {
            hour = 0;
        }

        double? alternative = !pm && !am && hour < 12 ? ((hour + 12) * 60) + minute : null;
        return new Quantity(QuantityKind.Clock, (hour * 60) + minute, alternative, written);
    }

    private static readonly HashSet<string> NotNouns = new(StringComparer.Ordinal) { "y", "e", "en", "de", "del", "a", "o", "hasta", "para", "que", "cuando", "pero", "porque", "por", "con", "si" };

    // "las 2 personas" counts people; "a las 2 y media", "a las 2 en punto" are hours.
    private static bool NounFollows(string after)
    {
        var trimmed = after.TrimStart();
        var length = 0;
        while (length < trimmed.Length && char.IsLetter(trimmed[length]))
        {
            length++;
        }

        return length > 0 && after.Length > trimmed.Length && !NotNouns.Contains(trimmed[..length]);
    }

    private static double? Multiplier(string after)
    {
        var match = MultiplierAfter().Match(after);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["w"].Value == "mil" ? 1_000 : 1_000_000;
    }

    /// <summary>The value of a written number: groups of three digits are thousands ("80.000", "80,000"); otherwise Colombian separators first, then English.</summary>
    internal static double? Value(string written)
    {
        if (ThousandsOnly().IsMatch(written) && written.Count(c => c is '.' or ',') > 0 && written.Where(c => c is '.' or ',').Distinct().Count() == 1)
        {
            return double.Parse(new string(written.Where(char.IsAsciiDigit).ToArray()), CultureInfo.InvariantCulture);
        }

        foreach (var (group, mark) in new[] { ('.', ','), (',', '.') })
        {
            if (Parse(written, group, mark) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static double? Parse(string written, char group, char mark)
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

        var decimals = parts.Length == 2 ? parts[1] : string.Empty;
        if (decimals.Contains(group) || (parts.Length == 2 && decimals.Length == 0))
        {
            return null;
        }

        var digits = string.Concat(whole);
        return double.TryParse(decimals.Length == 0 ? digits : digits + "." + decimals, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    // ---- The pre-pass: words to digits, slang, half hours, parts of the day ------------------------------------------------------------

    private static readonly Dictionary<string, int> Units = new(StringComparer.Ordinal)
    {
        ["cero"] = 0, ["un"] = 1, ["uno"] = 1, ["una"] = 1, ["dos"] = 2, ["tres"] = 3, ["cuatro"] = 4, ["cinco"] = 5, ["seis"] = 6, ["siete"] = 7,
        ["ocho"] = 8, ["nueve"] = 9, ["diez"] = 10, ["once"] = 11, ["doce"] = 12, ["trece"] = 13, ["catorce"] = 14, ["quince"] = 15, ["dieciseis"] = 16,
        ["dieciséis"] = 16, ["diecisiete"] = 17, ["dieciocho"] = 18, ["diecinueve"] = 19, ["veinte"] = 20, ["veintiun"] = 21, ["veintiuno"] = 21,
        ["veintiuna"] = 21, ["veintidos"] = 22, ["veintidós"] = 22, ["veintitres"] = 23, ["veintitrés"] = 23, ["veinticuatro"] = 24, ["veinticinco"] = 25,
        ["veintiseis"] = 26, ["veintiséis"] = 26, ["veintisiete"] = 27, ["veintiocho"] = 28, ["veintinueve"] = 29, ["treinta"] = 30, ["cuarenta"] = 40,
        ["cincuenta"] = 50, ["sesenta"] = 60, ["setenta"] = 70, ["ochenta"] = 80, ["noventa"] = 90, ["cien"] = 100, ["ciento"] = 100,
        ["doscientos"] = 200, ["doscientas"] = 200, ["trescientos"] = 300, ["trescientas"] = 300, ["cuatrocientos"] = 400, ["cuatrocientas"] = 400,
        ["quinientos"] = 500, ["quinientas"] = 500, ["seiscientos"] = 600, ["seiscientas"] = 600, ["setecientos"] = 700, ["setecientas"] = 700,
        ["ochocientos"] = 800, ["ochocientas"] = 800, ["novecientos"] = 900, ["novecientas"] = 900,
    };

    private static bool IsNumberWord(string word) => Units.ContainsKey(word) || word is "mil" or "millón" or "millon" or "millones" or "medio";

    internal static string Normalize(string lower)
    {
        var text = WordsToDigits(lower);
        text = Slang().Replace(text, m => m.Groups["n"].Value + (m.Groups["u"].Value == "palos" ? " millones" : " mil"));
        text = HalfHour().Replace(text, "30 minutos");
        text = HoursAndHalf().Replace(text, m => ((int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) * 60) + 30).ToString(CultureInfo.InvariantCulture) + " minutos");
        text = BareHourAndHalf().Replace(text, "90 minutos");
        text = ClockAndHalf().Replace(text, m => m.Groups["pre"].Value + m.Groups["n"].Value + (m.Groups["q"].Value == "media" ? ":30" : ":15"));
        text = Afternoon().Replace(text, m => m.Groups["n"].Value + " pm");
        text = Morning().Replace(text, m => m.Groups["n"].Value + " am");
        return text;
    }

    // Runs of number words become digits when they are 100 or more, carry a unit, or follow "las" (an hour).
    private static string WordsToDigits(string lower)
    {
        var words = WordPattern().Matches(lower).ToList();
        var replacements = new List<(int Start, int End, string Digits)>();
        for (var i = 0; i < words.Count; i++)
        {
            if (!IsNumberWord(words[i].Value) || words[i].Value is "mil" or "medio" && !(words[i].Value == "medio" && i + 1 < words.Count && words[i + 1].Value.StartsWith("mill", StringComparison.Ordinal)))
            {
                if (!(words[i].Value == "medio" && i + 1 < words.Count && words[i + 1].Value.StartsWith("mill", StringComparison.Ordinal)))
                {
                    continue;
                }
            }

            // "por ciento" is a percent sign, not a hundred.
            if (words[i].Value is "ciento" or "cien" && i > 0 && words[i - 1].Value == "por")
            {
                continue;
            }

            var j = i;
            var run = new List<string>();
            while (j < words.Count && (IsNumberWord(words[j].Value) || (words[j].Value == "y" && run.Count > 0 && Tens(run[^1]) && j + 1 < words.Count && Units.TryGetValue(words[j + 1].Value, out var unit) && unit is >= 1 and <= 9))
                   && (j == i || OnlySpaces(lower, words[j - 1].Index + words[j - 1].Length, words[j].Index)))
            {
                run.Add(words[j].Value);
                j++;
            }

            if (run.Count == 0)
            {
                continue;
            }

            // "millones" alone multiplies a number written in digits ("1,2 millones"): left as it is.
            if (!run.Any(w => Units.ContainsKey(w) || w == "medio"))
            {
                i = j - 1;
                continue;
            }

            var value = RunValue(run);
            var start = words[i].Index;
            var end = words[j - 1].Index + words[j - 1].Length;
            var after = lower[end..];
            var before = lower[..start];
            if (value is { } v && (v >= 100 || UnitAfterWords().IsMatch(after) || (ClockBefore().IsMatch(before) && v is >= 1 and <= 12)))
            {
                replacements.Add((start, end, v.ToString("0", CultureInfo.InvariantCulture)));
            }

            i = j - 1;
        }

        var builder = new System.Text.StringBuilder(lower);
        foreach (var (start, end, digits) in replacements.OrderByDescending(r => r.Start))
        {
            builder.Remove(start, end - start).Insert(start, digits);
        }

        return builder.ToString();
    }

    // "ochenta y cinco" joins a ten and a unit; "ciento ochenta y doscientos" is two numbers.
    private static bool Tens(string word) => Units.TryGetValue(word, out var value) && value is >= 20 and <= 90 && value % 10 == 0;

    private static bool OnlySpaces(string text, int from, int to) => text[from..to].All(c => c == ' ');

    private static double? RunValue(List<string> run)
    {
        double total = 0, current = 0;
        var millions = false;
        var milAfterMillions = false;
        for (var k = 0; k < run.Count; k++)
        {
            var word = run[k];
            if (word == "y")
            {
                continue;
            }

            if (word == "medio")
            {
                current = 0.5;
            }
            else if (word == "mil")
            {
                current = (current == 0 ? 1 : current) * 1000;
                total += current;
                current = 0;
                milAfterMillions |= millions;
            }
            else if (word is "millón" or "millon" or "millones")
            {
                total = (total + (current == 0 ? 1 : current)) * 1_000_000;
                current = 0;
                millions = true;
            }
            else if (word is "cien" or "ciento" || word.EndsWith("ientos", StringComparison.Ordinal) || word.EndsWith("ientas", StringComparison.Ordinal))
            {
                current += Units[word];
            }
            else
            {
                current += Units[word];
            }
        }

        // "un millón doscientos" is said for 1,200,000.
        return millions && !milAfterMillions && current is > 0 and < 1000 ? total + (current * 1000) : total + current;
    }

    private static readonly string[] WeekdayNames = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];

    private static IEnumerable<Quantity> RelativeDays(string lower, DateOnly today)
    {
        foreach (Match match in RelativeDay().Matches(lower))
        {
            var word = match.Groups["w"].Value;
            DateOnly? day = word switch
            {
                "hoy" => today,
                "pasado mañana" or "pasado manana" => today.AddDays(2),
                "mañana" or "manana" => today.AddDays(1),
                _ => null,
            };
            if (day is null)
            {
                var index = Array.IndexOf(WeekdayNames, word.Replace("miercoles", "miércoles", StringComparison.Ordinal).Replace("sabado", "sábado", StringComparison.Ordinal));
                if (index < 0)
                {
                    continue;
                }

                day = today.AddDays(((index - (int)today.DayOfWeek) + 7) % 7);
            }

            yield return new Quantity(QuantityKind.Date, (day.Value.Month * 100) + day.Value.Day, null, word) { Day = day };
        }
    }

    private const RegexOptions Linear = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^\s?x\s?\d+", Linear)]
    private static partial Regex TimesAfter();

    [GeneratedRegex(@"[a-záéíóúñü]+", Linear)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"^\s*(pesos|cop|por ciento|%|minutos|minuto|min|horas|hora|mil|millones|millón|millon|lucas)(\W|$)", Linear)]
    private static partial Regex UnitAfterWords();

    [GeneratedRegex(@"(?<n>\d+)\s*(?<u>k|lucas|palos)(\W|$)", Linear)]
    private static partial Regex Slang();

    [GeneratedRegex(@"\bmedia hora\b", Linear)]
    private static partial Regex HalfHour();

    [GeneratedRegex(@"\b(?<n>\d+) horas? y media\b", Linear)]
    private static partial Regex HoursAndHalf();

    [GeneratedRegex(@"\bhora y media\b", Linear)]
    private static partial Regex BareHourAndHalf();

    [GeneratedRegex(@"(?<pre>\blas? )(?<n>\d{1,2}) y (?<q>media|cuarto)\b", Linear)]
    private static partial Regex ClockAndHalf();

    [GeneratedRegex(@"(?<n>\d{1,2}(:\d{2})?) de la (tarde|noche)\b", Linear)]
    private static partial Regex Afternoon();

    [GeneratedRegex(@"(?<n>\d{1,2}(:\d{2})?) de la (mañana|manana)\b", Linear)]
    private static partial Regex Morning();

    // "mañana" as a day (not "en la mañana", "por la mañana", "de la mañana": the morning).
    [GeneratedRegex(@"(?<![a-zñ])(?<w>pasado mañana|pasado manana|hoy|lunes|martes|miércoles|miercoles|jueves|viernes|sábado|sabado|domingo)(?![a-zñ])|(?<!(la|las) )(?<![a-zñ])(?<w>mañana|manana)(?![a-zñ])", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeDay();

    [GeneratedRegex(@"(vale|valen|cuesta|cuestan|precio|valor|sale en|sale a|queda en|quedan en|está en|esta en|están en|estan en|cobramos|cobran)\s*\$?\s*$", Linear)]
    private static partial Regex PriceWordBefore();

    [GeneratedRegex(@"^\d{1,3}([.,]\d{3})+$", Linear)]
    private static partial Regex ThousandsOnly();

    [GeneratedRegex(@"\d+(?:[.,]\d+)*", Linear)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^:(?<m>[0-5]\d)\s*(?<ampm>a\.?\s?m\.?|p\.?\s?m\.?)?", Linear)]
    private static partial Regex ClockAfter();

    [GeneratedRegex(@"^\s*(?<ampm>a\.?\s?m\.?|p\.?\s?m\.?)(\W|$)", Linear)]
    private static partial Regex AmPmAfter();

    [GeneratedRegex(@"(\$|cop|cop\$)\s*$", Linear)]
    private static partial Regex CurrencyBefore();

    [GeneratedRegex(@"^\s*(?<w>mil|millones|millón|millon)(\W|$)", Linear)]
    private static partial Regex MultiplierAfter();

    [GeneratedRegex(@"^\s*(pesos|cop)(\W|$)", Linear)]
    private static partial Regex PesosAfter();

    [GeneratedRegex(@"^\s*(%|por ciento)", Linear)]
    private static partial Regex PercentAfter();

    [GeneratedRegex(@"^\s*(minutos|minuto|min)(\W|$)", Linear)]
    private static partial Regex MinutesAfter();

    [GeneratedRegex(@"^\s*(horas|hora|h)(\W|$)", Linear)]
    private static partial Regex HoursAfter();

    [GeneratedRegex(@"^\s+(al\s+\d{1,2}\s+)?de\s+(?<month>enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|octubre|noviembre|diciembre)", Linear)]
    private static partial Regex DateAfter();

    [GeneratedRegex(@"(las|la)\s+$", Linear)]
    private static partial Regex ClockBefore();
}
