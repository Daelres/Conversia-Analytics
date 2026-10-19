using System.Text;
using System.Text.RegularExpressions;

namespace Conversia.TestSupport;

/// <summary>
/// Finds English user-interface wording in a piece of text. Compiled into the test projects that check
/// "all text shown to users is Spanish" (linked file, so every project applies the very same rules).
///
/// <para><b>How it matches.</b> The <see cref="Denylist"/> holds English words and phrases that a Spanish interface
/// never uses. A term only matches as a whole word, case-insensitively: <c>Users</c> is flagged, <c>UsersManage</c>
/// (an enum name inside a <c>data-*</c> value or a route) and <c>tenantName</c> are not, and neither is a word that
/// merely starts or ends with a term. Nothing is matched by substring.</para>
///
/// <para><b>What is deliberately NOT on the list</b> (legitimate in a Spanish interface, or ambiguous with Spanish):
/// <c>stock</c>, <c>email</c>, <c>SKU</c>, <c>login</c>, <c>logout</c>, <c>JWT</c>, <c>API</c>, <c>Docker</c>,
/// <c>Blazor</c>, <c>token</c>, <c>tenant</c>, <c>tour</c>, <c>workspace</c>, <c>manager</c>, <c>error</c>, <c>total</c>,
/// <c>menu</c>, <c>status</c>, <c>version</c> (loanwords and technology names), and short function words that are also
/// Spanish or too ambiguous (<c>no</c>, <c>has</c>, <c>me</c>, <c>as</c>, <c>do</c>, <c>in</c>, <c>on</c>, <c>of</c>, <c>or</c>,
/// <c>a</c>, <c>to</c>, <c>is</c>, <c>it</c>, <c>can</c>, <c>may</c>, <c>by</c>).</para>
///
/// <para><b>Allowlist</b> (text removed before matching, because it is a name or data and not interface wording):
/// e-mail addresses, URLs, the product name <c>Conversia[.X]</c>, the technology names <c>ASP.NET Core</c>,
/// <c>.NET</c>, <c>PostgreSQL</c>, <c>Docker Compose</c>. Callers additionally exclude
/// user-entered data cells, <c>data-*</c> attributes and the lines of <c>&lt;code&gt;</c>/<c>&lt;pre&gt;</c> blocks that are a route, JSON,
/// a command line or a key name; English prose inside those elements is still scanned (see the HTML extractor in the Web tests).</para>
///
/// <para><b>Homograph checks for the later additions</b> (Close ... Email address): each was checked against Spanish and against the
/// Spanish texts of the interface. <c>Roles</c> was dropped because it is the Spanish plural of <c>rol</c>. <c>Continue</c> only collides with an
/// unaccented misspelling of <c>continúe</c> (matching is accent-sensitive, so the correct <c>continúe</c> is not flagged); <c>Open</c> and
/// <c>Enter</c> have no Spanish meaning and only match as whole words (<c>OpenAPI</c> is not flagged).</para>
/// </summary>
internal static class EnglishLeaks
{
    /// <summary>
    /// Regular-expression fragments, each a whole English word/phrase (inflections spelled out). Phrases first, then words,
    /// then English function words that never occur in Spanish text.
    /// </summary>
    public static readonly IReadOnlyList<string> Denylist = new[]
    {
        // ---- phrases ----
        @"sign[ -]?(in|out|up)", @"log[ -](in|out)", @"something went wrong", @"try again", @"go back", @"back to",
        @"not found", @"are you sure", @"[a-z]+n't",

        // ---- actions and navigation ----
        @"regist(er|ered|ering|ration)", @"submit", @"cancel", @"delet(e|ed|ing)", @"edit(s|ed|ing)?", @"creat(e|ed|ing)",
        @"sav(e|ed|ing)", @"search(es|ed|ing)?", @"previous", @"next", @"back", @"welcome", @"hello", @"sorry", @"thanks",
        @"add", @"new", @"updat(e|ed|ing)", @"reload", @"show", @"hide", @"click", @"home", @"loading",

        // ---- nouns of the application ----
        @"passwords?", @"username", @"users?", @"products?", @"names?", @"prices?", @"pages?", @"template", @"owners?",
        @"viewers?", @"permissions?", @"accounts?", @"actions?", @"catalog", @"features?", @"description", @"items?",
        @"list", @"details?", @"requests?", @"access", @"description", @"starter",

        // ---- verbs and adjectives of messages ----
        @"unauthori[sz]ed", @"forbidden", @"invalid", @"valid", @"required", @"already", @"please", @"unavailable",
        @"currently", @"later", @"again", @"attempts?", @"wrong", @"failed", @"success(ful|fully)?", @"modified", @"someone",
        @"else", @"latest", @"exists?", @"found", @"manage[sd]?", @"view(s|ed|er|ing)?", @"grant(s|ed)?", @"revoke[sd]?",
        @"allowed", @"held", @"must", @"should", @"cannot", @"between", @"characters?", @"least", @"exceed(s|ed)?",
        @"recogni[sz]ed", @"converted", @"field", @"values?", @"numbers?", @"body", @"try", @"now", @"things",

        // ---- added after review: common UI words and phrases the first list missed ("Roles" is Spanish, so only "role") ----
        @"close", @"remove", @"open", @"enter", @"select", @"sort", @"showing", @"settings", @"profile", @"dashboard", @"role",
        @"retry", @"continue", @"confirm", @"download", @"upload",
        @"choose\s+a\s+file", @"remember\s+me", @"session\s+expired", @"e-?mail\s+address",

        // ---- yes / none ----
        @"yes", @"none",

        // ---- English function words (no Spanish homograph) ----
        @"the", @"and", @"your", @"you", @"with", @"this", @"that", @"was", @"were", @"are", @"have", @"will", @"from",
        @"does", @"for", @"not", @"been", @"what", @"when", @"where", @"which", @"while",
    };

    /// <summary>The texts the frameworks produce when nobody localised them. Any occurrence (as whole words) is a leak: "Conflict" is, "Conflicto" is not.</summary>
    public static readonly IReadOnlyList<string> FrameworkDefaults = new[]
    {
        "One or more validation errors occurred.",
        "A non-empty request body is required.",
        "The JSON value could not be converted to",
        "The value '",
        "is not valid.",
        "is not a valid e-mail address.",
        "field is required.",
        "An error occurred while processing your request.",
        "Unauthorized",
        "Forbidden",
        "Bad Request",
        "Not Found",
        "Too Many Requests",
        "Internal Server Error",
        "Unsupported Media Type",
        "Method Not Allowed",
        "Not Acceptable",
        "Unprocessable Entity",
        "Request Timeout",
        "Conflict",
        "Request body too large",
        "Failed to read the request form",
        "The input does not contain any JSON tokens",
        "Unexpected character encountered",
        "is an invalid start of a value",
    };

    private static readonly Regex Matcher = new(
        @"(?<![\p{L}\p{N}_])(?:" + string.Join("|", Denylist) + @")(?![\p{L}\p{N}_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DefaultsMatcher = new(
        string.Join("|", FrameworkDefaults.Select(d =>
            (char.IsLetterOrDigit(d[0]) ? @"(?<![\p{L}\p{N}_])" : string.Empty)
            + Regex.Escape(d)
            + (char.IsLetterOrDigit(d[^1]) ? @"(?![\p{L}\p{N}_])" : string.Empty))),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex[] Allowlist =
    {
        new(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", RegexOptions.Compiled),                // e-mail addresses (data)
        new(@"https?://\S+", RegexOptions.Compiled),                                // URLs
        new(@"DR\.Template(\.\w+)*", RegexOptions.Compiled),                       // the product name
        new(@"ASP\.NET Core|\.NET|PostgreSQL|Docker Compose|Entity Framework( Core)?", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    private static readonly Regex SpanishMarker = new(
        @"(?<![\p{L}\p{N}_])(de|la|el|los|las|un|una|que|para|con|por|del|al|tu|tus|su|sus|este|esta|ya|se|en|sesión|correo|contraseña|usuarios?|productos?|inicio|cuentas?|permisos?|precio|nombre|uno|más|errores?|validación|es|son|no|debe|deben|obligatori[oa]s?|requerid[oa]s?|válid[oa]s?|inválid[oa]s?|campo|formato|caracteres|existe|encontrad[oa]|inténtalo|intentos|y|o|pero|como|si|cada|todos|todas|ahora|aquí|nuevo|nueva|ver|crear|editar|eliminar|guardar|cancelar|volver|detalles?|acciones?|descripción)(?![\p{L}\p{N}_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The denylisted words/phrases found in <paramref name="text"/> (distinct, lower-cased); empty when clean.</summary>
    public static IReadOnlyList<string> Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var cleaned = Allowed(text);
        var leaks = Matcher.Matches(cleaned).Select(m => m.Value.ToLowerInvariant()).ToList();
        leaks.AddRange(DefaultsMatcher.Matches(cleaned).Select(m => $"[framework default] {m.Value}"));
        return leaks.Distinct().ToList();
    }

    /// <summary>True when <paramref name="text"/> equals or contains a known untranslated framework message.</summary>
    public static bool IsFrameworkDefault(string? text)
        => !string.IsNullOrWhiteSpace(text) && DefaultsMatcher.IsMatch(text.Normalize(NormalizationForm.FormC));

    /// <summary>True when the text contains at least one common Spanish word (a positive check that Spanish was scanned, not nothing).</summary>
    public static bool LooksSpanish(string? text)
        => !string.IsNullOrWhiteSpace(text) && SpanishMarker.IsMatch(Allowed(text));

    /// <summary>
    /// Number of prose words the scanner actually looked at after the allowlist. Only whitespace-separated tokens that look like
    /// words count: a token containing a digit, a hyphen or an underscore is an identifier, number or timestamp (a GUID, a W3C trace id,
    /// <c>FER0-3f2a</c>, <c>2026-10-16</c>, <c>1.2.3</c>) and never counts. A text with a single token (no whitespace) is at most one word.
    /// </summary>
    public static int WordCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var tokens = Allowed(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var words = tokens.Where(t => !IsIdentifierLike(t) && t.Any(char.IsLetter)).ToList();
        return tokens.Length == 1
            ? words.Count
            : words.Sum(t => Regex.Matches(t, @"\p{L}+").Count);
    }

    /// <summary>True for a token that is an identifier/number/timestamp rather than a word: it contains a digit, a hyphen or an underscore.</summary>
    private static bool IsIdentifierLike(string token)
        => token.Any(c => char.IsDigit(c) || c is '-' or '_');

    private static string Allowed(string text)
    {
        var cleaned = text.Normalize(NormalizationForm.FormC);
        foreach (var allowed in Allowlist)
        {
            cleaned = allowed.Replace(cleaned, " ");
        }

        return cleaned;
    }
}
