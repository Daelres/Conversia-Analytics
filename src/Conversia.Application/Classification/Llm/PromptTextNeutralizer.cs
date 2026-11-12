using System.Text;
using System.Text.RegularExpressions;

namespace Conversia.Application.Classification.Llm;

/// <summary>
/// Makes a message body safe to place inside the conversation block (design D19, spec PS-4): customer and advisor text is data, and nothing
/// in it can start a line, imitate a line of another author, close the block or address a message.
/// <list type="bullet">
/// <item>The text is put in canonical form first (<see cref="TextSanitizer"/>: NFKC, no zero-width or other format characters), so fullwidth
/// <c>｜</c> or <c>＜</c> and invisible characters cannot slip past the rules below.</item>
/// <item>CR, LF, tabs, other control characters and U+2028/U+2029 become spaces; runs of white space become one space; the ends are trimmed.</item>
/// <item>Text that imitates the gap marker (<c>[... 3 messages omitted]</c>, also written with "…") gets parentheses instead of brackets.</item>
/// <item><c>&lt;conversation&gt;</c> and <c>&lt;/conversation&gt;</c> (any case, inner spaces, or as HTML entities) become <c>[tag]</c>.</item>
/// <item>The old line tag <c>[#9][A]</c> becomes <c>(#9)(A)</c>, and a line prefix of <see cref="PromptFormat"/> (<c>9 A:</c>) becomes <c>9 A;</c>.</item>
/// <item><c>|</c> (the segment separator) becomes <c>/</c>, and a body that would read as a back-reference (<c>=3</c>) becomes <c>= 3</c>.</item>
/// </list>
/// Line numbers and roles are generated only from the real snapshot, never from the text. Idempotent.
/// </summary>
public static partial class PromptTextNeutralizer
{
    public static string Neutralize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = CollapseWhitespace(TextSanitizer.Sanitize(text));
        result = ConversationTag().Replace(result, "[tag]");
        result = GapMarker().Replace(result, "($1)");
        result = OldLineTag().Replace(result, m => $"(#{m.Groups[1].Value})({m.Groups[2].Value})");
        result = LinePrefix().Replace(result, "$1 $2;");
        result = result.Replace('|', '/');
        result = BackReference().Replace(result, "= $1");
        return CollapseWhitespace(result);
    }

    /// <summary>Control characters and line or paragraph separators become spaces, runs of white space one space, and the ends are trimmed.</summary>
    public static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '\u2028' or '\u2029')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"(?:<|&lt;)\s*/?\s*conversation\s*(?:>|&gt;)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConversationTag();

    [GeneratedRegex(@"\[(\s*(?:\u2026|\.\.\.)\s*\d+\s+messages?\s+omitted\s*)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GapMarker();

    [GeneratedRegex(@"\[#(\d+)\]\[([CAca])\]", RegexOptions.CultureInvariant)]
    private static partial Regex OldLineTag();

    [GeneratedRegex(@"(?<![^\s])(\d+) ([CAca]):", RegexOptions.CultureInvariant)]
    private static partial Regex LinePrefix();

    [GeneratedRegex(@"^=(\d)", RegexOptions.CultureInvariant)]
    private static partial Regex BackReference();
}
