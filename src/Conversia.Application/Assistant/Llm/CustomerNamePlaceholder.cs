using System.Globalization;
using System.Text.RegularExpressions;
using Conversia.Application.Classification.Llm;

namespace Conversia.Application.Assistant.Llm;

/// <summary>
/// The customer's name never reaches the model (docs/ASSISTANT.md, "Prompt design"): the model writes <c>{nombre}</c> where a salesperson
/// would say the name, and it is filled here, locally, with the first name of the saved contact when that looks like a name; otherwise the
/// placeholder and the comma or space that went with it are removed, so "¡Hola {nombre}!" becomes "¡Hola!". The customer's pseudonym
/// (<c>[CLIENTE]</c>) is treated the same way.
/// </summary>
public static partial class CustomerNamePlaceholder
{
    public const string Token = "{nombre}";

    private const int MaxNameLength = 20;

    // Display names that are not a person's first name (WhatsApp defaults, roles, business words).
    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "cliente", "clienta", "usuario", "usuaria", "whatsapp", "user", "contacto", "desconocido", "unknown", "empresa", "tienda", "spa", "sr", "sra", "dr", "dra",
        "senor", "senora", "doctor", "doctora", "mama", "papa", "amor", "bebe", "info", "ventas", "admin", "test", "prueba",
    };

    /// <summary>The first word of the saved name, capitalized, when it is 2 to 20 letters (a hyphen inside allowed) and not a generic word; else null.</summary>
    public static string? SafeFirstName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var first = TextSanitizer.Sanitize(displayName).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (first.Length is < 2 or > MaxNameLength || !NameShape().IsMatch(first) || NotNames.Contains(AssistantRules.Fold(first)))
        {
            return null;
        }

        var parts = first.Split('-');
        return string.Join('-', parts.Select(p => char.ToUpper(p[0], CultureInfo.GetCultureInfo("es-CO")) + p[1..].ToLower(CultureInfo.GetCultureInfo("es-CO"))));
    }

    /// <summary>Every way the model may write the placeholder ({Nombre}, { nombre }, [CLIENTE]) as <see cref="Token"/>: what the checks read.</summary>
    public static string Normalize(string text) => Placeholder().Replace(text, Token);

    /// <summary>The text with every placeholder filled with <paramref name="firstName"/>, or removed naturally when it is null.</summary>
    public static string Fill(string text, string? firstName)
    {
        ArgumentNullException.ThrowIfNull(text);
        var withToken = Placeholder().Replace(text, Token);
        if (!withToken.Contains(Token, StringComparison.Ordinal))
        {
            return text;
        }

        if (firstName is not null)
        {
            return withToken.Replace(Token, firstName, StringComparison.Ordinal);
        }

        var removed = Leading().Replace(withToken, m => m.Groups["open"].Value);
        removed = WithComma().Replace(removed, string.Empty);
        removed = WithSpace().Replace(removed, string.Empty);
        removed = removed.Replace(Token, string.Empty, StringComparison.Ordinal).Trim();
        return removed.Length > 0 && char.IsLower(removed[0]) ? char.ToUpper(removed[0], CultureInfo.GetCultureInfo("es-CO")) + removed[1..] : removed;
    }

    [GeneratedRegex(@"^[\p{L}]+(-[\p{L}]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NameShape();

    // {nombre}, {Nombre}, { nombre }, [CLIENTE]
    [GeneratedRegex(@"\{\s*nombre\s*\}|\[CLIENTE\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    // At the start of the text or of a sentence, with its comma: "{nombre}, axilas..." -> "axilas..."
    [GeneratedRegex(@"(?<open>^|[.!?¡¿]\s*)\{nombre\}\s*,?\s*", RegexOptions.CultureInvariant)]
    private static partial Regex Leading();

    // ", {nombre}," or ", {nombre}" before punctuation: "Claro, {nombre}, axilas" -> "Claro, axilas"
    [GeneratedRegex(@",\s*\{nombre\}(?=\s*[,.!?])", RegexOptions.CultureInvariant)]
    private static partial Regex WithComma();

    // " {nombre}" before punctuation: "¡Hola {nombre}!" -> "¡Hola!"
    [GeneratedRegex(@"\s+\{nombre\}", RegexOptions.CultureInvariant)]
    private static partial Regex WithSpace();
}
