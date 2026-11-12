using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Conversia.Domain.Enums;

namespace Conversia.Application.Classification.Llm;

/// <summary>
/// Replaces personal data with stable per-conversation placeholders before any text leaves the process (design D9, spec CC-8). Nothing is
/// kept: the value-to-placeholder mapping lives only inside one call, so there is no restoration and nothing to persist. In order:
/// <list type="number">
/// <item>The text is put in canonical form (<see cref="TextSanitizer"/>: NFKC, ASCII digits, no zero-width characters, ASCII spaces), and the
/// letter O used as a zero inside a long digit run (<c>3OO1234567</c>) becomes 0.</item>
/// <item>A phone split across two consecutive messages of the same side (<c>300 123</c> / <c>4567</c>) is masked in both.</item>
/// <item>Emails (accented, spaced <c>juan @ gmail . com</c> or spelled <c>juan arroba gmail punto com</c>) become <c>[EMAIL]</c>, links and
/// domains <c>[URL]</c>, social handles (<c>@ana</c>, or after <c>IG:</c>, <c>insta es</c>, <c>FB:</c>, <c>TikTok</c>) <c>[USUARIO]</c>, and
/// business-scoped user ids (<c>CO.1349…</c>) a phone placeholder.</item>
/// <item>Numbers: digit groups joined by up to three of <c>space . , / -</c>, also glued to letters (<c>cel3001234567</c>). First match wins:
///   a known number of the snapshot (the contact's WhatsApp id, the channel number) in any format is <c>[TEL_n]</c>;
///   after an identity cue (<c>cédula</c>, <c>cc</c>, <c>c.c.</c>, <c>documento</c>, <c>identificación</c>, <c>nit</c>, <c>ti</c>, <c>pasaporte</c>), 6 to 10 digits are <c>[ID]</c>;
///   a date (<c>12/05/1990</c>, <c>05-11-2026</c>, <c>2026-11-05</c>) is kept;
///   a strong phone shape is <c>[TEL_n]</c> (a <c>+</c>/<c>00</c> prefix with 8 to 15 digits, a Colombian mobile, <c>57</c> and a mobile, or 7 to 10 digits after a phone cue);
///   groups joined by commas or slashes that are none of those are read one by one (a list of prices is not a phone);
///   57 with 7 to 10 more digits, or 11 to 15 bare digits, are <c>[TEL_n]</c>;
///   a number with a currency cue next to it (<c>$</c>, <c>COP</c>, <c>USD</c>, <c>pesos</c>, <c>de pesos</c>, <c>mil</c>, <c>millones</c>, <c>k</c>), or a dotted
///   thousands number (<c>1.200.000</c>) with a price cue near it (<c>cuesta</c>, <c>vale</c>, <c>precio</c>, <c>valor</c>, <c>paquete</c>, <c>sesión</c>...), is an amount and is kept;
///   any other run of 7 or more digits is <c>[ID]</c>; shorter runs are kept.</item>
/// <item>Names, whole tokens, ignoring case and accents: the contact's display name is <c>[CLIENTE]</c>, the advisor's <c>[ASESOR]</c>, the branch
/// <c>[SEDE]</c>. A full name is always masked; a single token when it has at least three letters and is not an ordinary word of the stoplist.</item>
/// </list>
/// The same value always gets the same placeholder within a conversation (<c>[TEL_1]</c>, <c>[TEL_2]</c> in order of first appearance). Accepted
/// residual risk (docs/decisiones.md): other names written in the text, nicknames and diminutives, street addresses, and a number split across
/// more than two messages or across sides.
/// </summary>
public static partial class Pseudonymizer
{
    public const string Customer = "[CLIENTE]";
    public const string Advisor = "[ASESOR]";
    public const string Branch = "[SEDE]";
    public const string KnownName = "[NOMBRE]";
    public const string Id = "[ID]";
    public const string Email = "[EMAIL]";
    public const string Url = "[URL]";
    public const string Handle = "[USUARIO]";

    /// <summary>A single name token shorter than this is never replaced on its own (only as part of the full name).</summary>
    public const int MinNameTokenLength = 3;

    /// <summary>Names that are ordinary Spanish words: replaced only as part of the full name.</summary>
    public static readonly IReadOnlySet<string> NameStoplist = new HashSet<string>(StringComparer.Ordinal)
    {
        "sol", "rosa", "luz", "paz", "mar", "dulce", "flor", "cielo", "angel", "gloria", "rey", "leon", "blanca", "clara", "pilar",
        "amparo", "consuelo", "esperanza", "felicidad", "milagros", "rocio", "aurora", "victoria", "alegria",
    };

    // Generic top-level domains accepted without a path; any other two-letter country code is accepted too, except ordinary Spanish words.
    private static readonly HashSet<string> Domains = new(StringComparer.Ordinal)
    {
        "com", "net", "org", "info", "biz", "edu", "gov", "io", "app", "shop", "store", "site", "online", "link", "page", "dev", "xyz",
        "club", "bio", "art", "digital", "social", "clinic", "salon", "beauty", "spa", "health", "care", "tienda", "web", "website", "blog",
        "live", "tv", "ai", "me", "ly", "co",
    };

    private static readonly HashSet<string> SpanishTwoLetterWords = new(StringComparer.Ordinal)
    {
        "la", "lo", "el", "de", "es", "no", "si", "ya", "mi", "tu", "te", "se", "en", "un", "al", "le", "me", "ni", "da", "va", "ve", "ok",
        "su", "yo", "nos", "tv",
    };

    /// <summary>The snapshot with every message text masked, and no known value left on it (no branch, names, numbers).</summary>
    public static ConversationSnapshot Pseudonymize(ConversationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var session = new Session(snapshot);
        var texts = snapshot.Messages.Select(m => m.Text is null ? null : OAsZero().Replace(TextSanitizer.Sanitize(m.Text), ZeroForO)).ToArray();
        session.MaskSplitNumbers(texts, [.. snapshot.Messages.Select(m => m.Direction)]);
        var messages = snapshot.Messages.Select((m, i) => texts[i] is null ? m : m with { Text = session.Mask(texts[i]!) }).ToList();
        return snapshot with
        {
            Messages = messages,
            BranchName = null,
            KnownNames = null,
            KnownNumbers = null,
            ContactName = null,
            AdvisorName = null,
        };
    }

    private sealed class Session
    {
        private readonly Dictionary<string, string> _phones = new(StringComparer.Ordinal);
        private readonly HashSet<string> _knownNumbers;
        private readonly List<(Regex Pattern, string Placeholder)> _names = [];

        public Session(ConversationSnapshot snapshot)
        {
            _knownNumbers = [.. snapshot.Numbers.Select(n => PhoneKey(Digits(TextSanitizer.Sanitize(n)))).Where(k => k.Length >= 7)];

            var people = new List<(string Name, string Placeholder, bool Tokens)>();
            if (!string.IsNullOrWhiteSpace(snapshot.ContactName))
            {
                people.Add((snapshot.ContactName, Customer, true));
            }

            if (!string.IsNullOrWhiteSpace(snapshot.AdvisorName))
            {
                people.Add((snapshot.AdvisorName, Advisor, true));
            }

            if (!string.IsNullOrWhiteSpace(snapshot.BranchName))
            {
                people.Add((snapshot.BranchName, Branch, false));
            }

            foreach (var other in snapshot.Names.Where(n => !string.IsNullOrWhiteSpace(n) && !people.Any(p => p.Name == n)))
            {
                people.Add((other, KnownName, false));
            }

            // Full names first (longest first), then single tokens: "Maria Perez" before "maria".
            var full = new List<(string Folded, string Placeholder)>();
            var tokens = new List<(string Folded, string Placeholder)>();
            foreach (var (name, placeholder, byToken) in people)
            {
                var parts = Fold(TextSanitizer.Sanitize(name)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Any(char.IsLetterOrDigit)).ToArray();
                if (parts.Length == 0)
                {
                    continue;
                }

                var tokenEligible = parts.Where(p => p.Length >= MinNameTokenLength && !NameStoplist.Contains(p)).ToList();
                if (parts.Length > 1 || !byToken || tokenEligible.Count > 0)
                {
                    full.Add((string.Join(' ', parts), placeholder));
                }

                if (byToken)
                {
                    tokens.AddRange(tokenEligible.Select(t => (t, placeholder)));
                }
            }

            foreach (var (folded, placeholder) in full.OrderByDescending(f => f.Folded.Length).Concat(tokens.OrderByDescending(t => t.Folded.Length)))
            {
                var pattern = string.Join(@"\s+", folded.Split(' ').Select(Regex.Escape));
                _names.Add((new Regex($@"(?<![\p{{L}}\p{{N}}_\[]){pattern}(?![\p{{L}}\p{{N}}_\]])", RegexOptions.CultureInvariant), placeholder));
            }
        }

        // "300 123" then "4567" from the same side: the two halves form a phone, so both are masked with one placeholder.
        public void MaskSplitNumbers(string?[] texts, MessageDirection[] directions)
        {
            for (var i = 0; i + 1 < texts.Length; i++)
            {
                if (texts[i] is not { } a || texts[i + 1] is not { } b || directions[i] != directions[i + 1])
                {
                    continue;
                }

                var tail = TrailingNumber().Match(a);
                var head = LeadingNumber().Match(b);
                if (!tail.Success || !head.Success)
                {
                    continue;
                }

                var digits = Digits(tail.Value) + Digits(head.Value);
                var prefixed = tail.Value.TrimStart().StartsWith('+');
                if (Digits(tail.Value).Length < 3 || !(IsKnown(digits) || IsStrongPhone(digits, prefixed)))
                {
                    continue;
                }

                var placeholder = Phone(PhoneKey(digits));
                texts[i] = a[..tail.Index] + (tail.Value.StartsWith(' ') ? " " : string.Empty) + placeholder;
                texts[i + 1] = placeholder + b[(head.Index + head.Length)..];
            }
        }

        public string Mask(string text)
        {
            text = EmailPattern().Replace(text, Email);
            text = SpacedEmailPattern().Replace(text, Email);
            text = SpelledEmailPattern().Replace(text, Email);
            text = FullUrlPattern().Replace(text, Url);
            text = CueHandlePattern().Replace(text, m =>
                m.Groups["sep"].Value.Contains(':') || m.Groups["sep"].Value.Contains("es", StringComparison.OrdinalIgnoreCase) || m.Groups["at"].Success
                || m.Groups["handle"].Value.Any(c => c is '.' or '_' || char.IsAsciiDigit(c))
                    ? m.Groups["cue"].Value + m.Groups["sep"].Value + Handle
                    : m.Value);
            text = HandlePattern().Replace(text, Handle);
            text = DomainPattern().Replace(text, m =>
            {
                var tld = m.Groups["tld"].Value.ToLowerInvariant();
                var accepted = m.Groups["path"].Success || Domains.Contains(tld) || m.Groups["labels"].Captures.Count >= 2
                               || (tld.Length == 2 && tld.All(char.IsAsciiLetter) && !SpanishTwoLetterWords.Contains(tld));
                return accepted ? Url : m.Value;
            });
            text = ScopedUserIdPattern().Replace(text, m => Phone(m.Value));
            text = NumberPattern().Replace(text, m => Number(m.Value, text[..m.Index], text[(m.Index + m.Length)..]));
            foreach (var (pattern, placeholder) in _names)
            {
                text = ReplaceFolded(text, pattern, placeholder);
            }

            return text;
        }

        private string Number(string value, string before, string after)
        {
            var digits = Digits(value);
            if (digits.Length >= 7 && IsKnown(digits))
            {
                return Phone(PhoneKey(digits));
            }

            var cueBefore = Fold(Tail(before, 30));
            if (digits.Length is >= 6 and <= 10 && IdCuePattern().IsMatch(cueBefore))
            {
                return Id;
            }

            if (DatePattern().IsMatch(value.Trim()))
            {
                return value;
            }

            var prefixed = value.StartsWith('+') || digits.StartsWith("00", StringComparison.Ordinal);
            if (IsStrongPhone(digits, prefixed) || (digits.Length is >= 7 and <= 10 && PhoneCuePattern().IsMatch(cueBefore)))
            {
                return Phone(PhoneKey(digits));
            }

            if (value.IndexOfAny([',', '/']) >= 0)
            {
                // A list ("60, 90, 120"), not one number: each group on its own.
                var result = new StringBuilder();
                var context = before;
                foreach (Match part in ListItem().Matches(value))
                {
                    var item = part.Groups["item"].Value;
                    result.Append(item.Any(char.IsAsciiDigit) ? Number(item, context, after) : item).Append(part.Groups["sep"].Value);
                    context += part.Value;
                }

                return result.ToString();
            }

            if ((digits.StartsWith("57", StringComparison.Ordinal) && digits.Length is >= 9 and <= 12) || (!prefixed && digits.Length is >= 11 and <= 15))
            {
                return Phone(PhoneKey(digits));
            }

            if (CurrencyBeforePattern().IsMatch(Tail(before, 8)) || CurrencyAfterPattern().IsMatch(after))
            {
                return value;
            }

            if (DottedThousands().IsMatch(value.Trim()) && (PriceCuePattern().IsMatch(Fold(Tail(before, 40))) || PriceAfterPattern().IsMatch(Fold(after.Length > 20 ? after[..20] : after))))
            {
                return value;
            }

            return digits.Length >= 7 ? Id : value;
        }

        private bool IsKnown(string digits) => _knownNumbers.Contains(PhoneKey(digits));

        private string Phone(string key)
        {
            if (!_phones.TryGetValue(key, out var placeholder))
            {
                placeholder = $"[TEL_{_phones.Count + 1}]";
                _phones[key] = placeholder;
            }

            return placeholder;
        }
    }

    // A Colombian mobile (3 and nine more digits), 57 and a mobile, or an international prefix with 8 to 15 digits.
    private static bool IsStrongPhone(string digits, bool prefixed)
        => (prefixed && digits.TrimStart('0').Length is >= 8 and <= 15)
           || (digits.Length == 10 && digits[0] == '3')
           || (digits.Length == 12 && digits.StartsWith("573", StringComparison.Ordinal));

    // The same number written with or without the Colombian country code, or with a 00 international prefix, is one number.
    private static string PhoneKey(string digits)
    {
        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        return digits.Length == 12 && digits.StartsWith("57", StringComparison.Ordinal) ? digits[2..] : digits;
    }

    private static string Digits(string value) => new([.. value.Where(char.IsAsciiDigit)]);

    private static string Tail(string before, int length) => before.Length > length ? before[^length..] : before;

    // Lower case without diacritics, one character for one character (so positions in the folded text are positions in the original).
    private static string Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsSurrogate(c))
            {
                folded.Append(c);
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            var decomposed = lower.ToString().Normalize(NormalizationForm.FormD);
            folded.Append(decomposed.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(decomposed[0]) != UnicodeCategory.NonSpacingMark ? decomposed[0] : lower);
        }

        return folded.ToString();
    }

    private static string ReplaceFolded(string text, Regex pattern, string placeholder)
    {
        var matches = pattern.Matches(Fold(text));
        if (matches.Count == 0)
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var position = 0;
        foreach (Match match in matches)
        {
            result.Append(text, position, match.Index - position).Append(placeholder);
            position = match.Index + match.Length;
        }

        return result.Append(text, position, text.Length - position).ToString();
    }

    // "3OO1234567", "3oo 123 4567": inside a run that starts with a digit and holds at least 4 real digits and 7 digits or O's, an O is a zero.
    private static string ZeroForO(Match run)
    {
        var digits = run.Value.Count(char.IsAsciiDigit);
        var symbols = run.Value.Count(c => char.IsAsciiDigit(c) || c is 'O' or 'o');
        return digits >= 4 && symbols >= 7 && symbols > digits ? run.Value.Replace('O', '0').Replace('o', '0') : run.Value;
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d(?:[0-9Oo]|[ .\-](?=[0-9Oo])){6,24}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex OAsZero();

    [GeneratedRegex(@"(?:\+\s?)?\d[\d .\-]*\d\s*$|(?<![\p{N}])\d+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNumber();

    [GeneratedRegex(@"^\s*\d[\d .\-]*\d|^\s*\d+(?![\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}\-]+(?:\.[\p{L}\p{N}\-]+)*\.\p{L}{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])[\p{L}\p{N}._%+\-]+\s*@\s*[\p{L}\p{N}\-]+(?:\s*\.\s*[\p{L}\p{N}\-]+)*?\s*\.\s*(?:com|co|net|org|es|edu|gov|mx|io|me|info|us)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpacedEmailPattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])[\p{L}\p{N}._\-]+\s+arroba\s+[\p{L}\p{N}\-]+(?:\s+punto\s+[\p{L}\p{N}\-]+)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpelledEmailPattern();

    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s\[\]]*[^\s\[\].,;:!?¿¡)]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FullUrlPattern();

    [GeneratedRegex(@"(?<cue>(?<![\p{L}])(?:ig|insta|instagram|fb|facebook|face|tiktok|tik tok|twitter|threads)(?![\p{L}]))(?<sep>\s*:\s*|\s+es\s+|\s+)(?<at>@)?(?<handle>[\p{L}\p{N}_](?:[\p{L}\p{N}_.]{1,28}[\p{L}\p{N}_]))(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CueHandlePattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_.@])@[\p{L}\p{N}_](?:[\p{L}\p{N}_.]{0,28}[\p{L}\p{N}_])?", RegexOptions.CultureInvariant)]
    private static partial Regex HandlePattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_.@\[\-])(?<labels>[\p{L}\p{N}](?:[\p{L}\p{N}\-]{0,61}[\p{L}\p{N}])?\.)+(?<tld>\p{L}{2,24})(?<path>/(?:[^\s\[\]]*[^\s\[\].,;:!?¿¡)])?)?(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"\b[A-Z]{2}\.\d{6,30}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ScopedUserIdPattern();

    // Digit groups joined by up to three of: space . , / - ; optionally in parentheses or after + ; never inside a placeholder ([TEL_1]).
    [GeneratedRegex(@"(?<![\p{N}_\[#])(?:\+\s?)?\(?\d+\)?(?:[ .,/\-]{1,3}\(?\d+\)?)*(?![\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    // One item of a list joined by commas or slashes, and the separator after it.
    [GeneratedRegex(@"(?<item>[^,/]+)(?<sep>[,/]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^(?:(?:0?[1-9]|[12]\d|3[01])[/\-.](?:0?[1-9]|[12]\d|3[01])[/\-.](?:\d{2}|\d{4})|\d{4}-(?:0?[1-9]|1[0-2])-(?:0?[1-9]|[12]\d|3[01]))$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^\d{1,3}(?:\.\d{3})+$", RegexOptions.CultureInvariant)]
    private static partial Regex DottedThousands();

    [GeneratedRegex(@"(?<![\p{L}])(?:cedula|cc|c\s?\.\s?c\.?|documento|identificacion|nit|ti|pasaporte)(?![\p{L}])[^\p{L}\p{N}]*(?:(?:es|de|mi|el|no|nro|numero|num)(?![\p{L}])[^\p{L}\p{N}]*){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdCuePattern();

    [GeneratedRegex(@"(?<![\p{L}])(?:cel|celular|whatsapp|wsp|wpp|tel|telefono|numero|llamame|escribeme|marcame)(?![\p{L}])[^\p{L}\p{N}]*(?:(?:es|de|mi|al|el|a)(?![\p{L}])[^\p{L}\p{N}]*){0,3}$|(?<![\p{L}])(?:cel|celular|whatsapp|wsp|wpp|tel|telefono)$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneCuePattern();

    [GeneratedRegex(@"(?:\$|\bCOP|\bUSD)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyBeforePattern();

    [GeneratedRegex(@"^\s*(?:de\s+pesos\b|pesos\b|cop\b|usd\b|mil\b|millones\b|millón\b|millon\b|k\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyAfterPattern();

    [GeneratedRegex(@"(?<![\p{L}])(?:cuesta|cuestan|costo|vale|valen|valor|precio|precios|paquete|paquetes|sesion|sesiones|total|pagar|pago|abono|cuota|cuotas|tarifa|pesos|cop|\$)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex PriceCuePattern();

    [GeneratedRegex(@"^\s*(?:de\s+)?(?:pesos|cop|mil|millones)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex PriceAfterPattern();
}
