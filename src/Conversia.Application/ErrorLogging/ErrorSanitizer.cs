using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Conversia.Application.ErrorLogging;

/// <summary>Pure, idempotent redaction and truncation applied to everything the error registry stores.</summary>
/// <remarks>
/// The input may be hostile (the Web reports text it did not write), so every pattern is matched by the non-backtracking
/// engine (linear time) under a timeout; if the timeout ever fires the whole value is replaced by <see cref="Redacted"/>.
/// </remarks>
public static class ErrorSanitizer
{
    /// <summary>Replaces the value of a secret.</summary>
    public const string Redacted = "***";

    /// <summary>Replaces a JWT-shaped string.</summary>
    public const string RedactedJwt = "[jwt]";

    /// <summary>Visible mark that a field was cut; the result of a truncation ends with it and is never longer than the limit.</summary>
    public const string TruncationMarker = "…[truncado]";

    private const string SensitiveKeys =
        "password|pwd|passwd|secret|apikey|api-key|api_key|token|access_token|refresh_token|authorization|signingkey|client_secret|connectionstring|user[ \\t]+id"
        + "|access[-_]?key|private[-_]?key|subscription[-_]?key";

    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    // key, separator, then the value (groups instead of back-references keep it NonBacktracking-compatible):
    //  - double-quoted: runs to its closing quote; "\x" is an escape and "" a literal quote (so "say ""hi"" there" is one value);
    //  - single-quoted: runs to its closing quote; '' is a literal quote (so 'it''s' is one value);
    //  - JSON escaped inside a string (\"x\", \\"x\\", ...): opens and closes at any run of backslashes plus a quote, whatever its depth;
    //  - unquoted: up to whitespace or ';', quotes and backslashes included, except a quote that ends the value (it is followed by
    //    ',', '}', ']' or whitespace/';'/end), so "password=abc" inside a JSON string does not eat the closing quote.
    // When the key itself carries a quote or backslash ("password":null) the unquoted value is JSON/dictionary style and also stops at
    // ',', '}' and ']'; a plain Password=a,b keeps its commas. An unterminated quote swallows the rest of the text: failing closed.
    private const string QuotedValue =
        "(?<dq>\")(?:\\\\[\\s\\S]|\"\"|[^\"\\\\])*(?<dqc>\")?"
        + "|(?<sq>')(?:[^']|'')*(?<sqc>')?"
        + "|(?<eq>\\\\+\")(?:[^\\\\]|\\\\+[^\\\\\"])*(?<eqc>\\\\+\")?";

    private const string AuthScheme = "(?:(?:bearer|basic)[ \\t]+)?";

    private static readonly Regex KeyValue = new(
        "(?<key>(?:" + SensitiveKeys + ")[\"'\\\\]+[ \\t]*[:=][ \\t]*)(?:" + QuotedValue + "|" + AuthScheme + "(?:[^\\s;\",}\\]]|\"+[^\\s;\",}\\]])*)"
        + "|(?<key>(?:" + SensitiveKeys + ")[ \\t]*[:=][ \\t]*)(?:" + QuotedValue + "|" + AuthScheme + "(?:[^\\s;\"]|\"+[^\\s;\",}\\]])*)",
        Options,
        MatchTimeout);

    private static readonly Regex BearerToken = new("\\b(?<scheme>bearer)[ \\t]+[^\\s;\"'\\\\]+", Options, MatchTimeout);

    private static readonly Regex Jwt = new("eyJ[A-Za-z0-9_-]+\\.ey[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]*", Options, MatchTimeout);

    // Judged on the decoded key with case and "_", "-", "." and spaces removed, so newPassword, access_token, X-Api-Key and
    // client.secret are all recognised. A key is sensitive if it contains one of these (the free-text KeyValue path matches the
    // same words right before the separator).
    private static readonly string[] SensitiveQueryFragments =
    [
        "password", "passwd", "pwd", "secret", "token", "apikey", "authorization", "signingkey", "connectionstring", "signature",
        "accesskey", "privatekey", "subscriptionkey",
    ];

    // Ordinary words that merely contain a sensitive fragment; removed from the key before it is judged.
    private static readonly string[] InnocuousWords = ["passwordless", "tokenizer", "tokenization", "tokenize", "secretary", "secretariat"];

    // Too common as a substring (keyword, monkey, sigma, design) to match by containment: sensitive only as the whole key.
    private static readonly string[] SensitiveWholeQueryKeys = ["key", "sig"];

    /// <summary>Free text (messages, stack traces, user agent, context): secrets redacted, control characters stripped, cut to <paramref name="maxLength"/>.</summary>
    public static string? SanitizeText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        try
        {
            var cleaned = StripControlCharacters(text, keepLineBreaksAndTabs: true);
            var redacted = RedactJwt(RedactBearer(RedactKeyValues(cleaned)));
            return Truncate(redacted, maxLength);
        }
        catch (RegexMatchTimeoutException)
        {
            return Truncate(Redacted, maxLength);
        }
    }

    /// <summary>A query string (with or without the leading <c>?</c>): keys and benign values kept, values of sensitive keys redacted, cut to <paramref name="maxLength"/>.</summary>
    public static string? SanitizeQuery(string? query, int maxLength)
    {
        if (string.IsNullOrEmpty(query))
        {
            return query;
        }

        try
        {
            var redacted = RedactJwt(RedactSensitiveQueryValues(StripControlCharacters(query, keepLineBreaksAndTabs: false)));
            return Truncate(redacted, maxLength);
        }
        catch (RegexMatchTimeoutException)
        {
            return Truncate(Redacted, maxLength);
        }
    }

    private static string RedactKeyValues(string text) => KeyValue.Replace(text, RedactedValue);

    // Keeps the key and the quotes that wrapped the value, whatever the value held.
    private static string RedactedValue(Match match)
    {
        var (open, close) = QuoteOf(match, "dq", "dqc") ?? QuoteOf(match, "sq", "sqc") ?? QuoteOf(match, "eq", "eqc") ?? (string.Empty, string.Empty);
        return match.Groups["key"].Value + open + Redacted + close;
    }

    private static (string Open, string Close)? QuoteOf(Match match, string open, string close)
        => match.Groups[open].Success ? (match.Groups[open].Value, match.Groups[close].Value) : null;

    private static string RedactBearer(string text) => BearerToken.Replace(text, "${scheme} " + Redacted);

    private static string RedactJwt(string text) => Jwt.Replace(text, RedactedJwt);

    private static string StripControlCharacters(string text, bool keepLineBreaksAndTabs)
    {
        var strip = keepLineBreaksAndTabs ? ControlCharacters : AllControlCharacters;
        var first = text.AsSpan().IndexOfAny(strip);
        if (first < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, first);
        for (var i = first; i < text.Length; i++)
        {
            if (!strip.Contains(text[i]))
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }

    private static readonly SearchValues<char> AllControlCharacters = SearchValues.Create(
        Enumerable.Range(0, 0xA0).Select(c => (char)c).Where(char.IsControl).ToArray());

    // A stack trace needs its line breaks and tabs; a query string never has them.
    private static readonly SearchValues<char> ControlCharacters = SearchValues.Create(
        Enumerable.Range(0, 0xA0).Select(c => (char)c).Where(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')).ToArray());

    private static string RedactSensitiveQueryValues(string query)
    {
        var builder = new StringBuilder(query.Length);
        var rest = query.AsSpan();
        var changed = false;

        while (true)
        {
            var end = rest.IndexOf('&');
            var pair = end < 0 ? rest : rest[..end];
            changed |= AppendPair(builder, pair);

            if (end < 0)
            {
                break;
            }

            builder.Append('&');
            rest = rest[(end + 1)..];
        }

        return changed ? builder.ToString() : query;
    }

    private static bool AppendPair(StringBuilder builder, ReadOnlySpan<char> pair)
    {
        var equals = pair.IndexOf('=');
        if (equals < 0 || equals == pair.Length - 1 || !IsSensitiveQueryKey(pair[..equals]))
        {
            builder.Append(pair);
            return false;
        }

        builder.Append(pair[..(equals + 1)]).Append(Redacted);
        return true;
    }

    private static bool IsSensitiveQueryKey(ReadOnlySpan<char> rawKey)
    {
        var decoded = Decode(rawKey.TrimStart('?'));
        if (string.Equals(decoded, "user id", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var key = Normalize(decoded);
        if (Array.IndexOf(SensitiveWholeQueryKeys, key) >= 0)
        {
            return true;
        }

        foreach (var word in InnocuousWords)
        {
            key = key.Replace(word, string.Empty, StringComparison.Ordinal);
        }

        foreach (var fragment in SensitiveQueryFragments)
        {
            if (key.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (c is not ('_' or '-' or '.') && !char.IsWhiteSpace(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static string Decode(ReadOnlySpan<char> key)
    {
        var text = key.ToString().Replace('+', ' ');
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        if (maxLength <= TruncationMarker.Length)
        {
            return text[..WholeCharacters(text, Math.Max(0, maxLength))];
        }

        var keep = WholeCharacters(text, maxLength - TruncationMarker.Length);
        return string.Concat(text.AsSpan(0, keep), TruncationMarker);
    }

    // A cut between the halves of a surrogate pair would leave an invalid UTF-16 string.
    private static int WholeCharacters(string text, int length)
        => length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
}
