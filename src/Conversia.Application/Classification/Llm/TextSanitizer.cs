using System.Globalization;
using System.Text;

namespace Conversia.Application.Classification.Llm;

/// <summary>
/// Puts text in one canonical form before it is masked or neutralized, so look-alike characters cannot slip a value or a delimiter past the
/// patterns: lone surrogates become U+FFFD, the text is NFKC-normalized (fullwidth <c>３</c> and <c>｜</c> become <c>3</c> and <c>|</c>), every
/// decimal digit of any script becomes its ASCII digit, format characters (zero-width spaces and joiners, soft hyphens, bidi marks) are
/// removed, and every other Unicode space (no-break, narrow, thin, ideographic) becomes an ASCII space. Line breaks and tabs are kept.
/// Pure and idempotent.
/// </summary>
public static class TextSanitizer
{
    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var valid = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                valid.Append(c).Append(text[++i]);
            }
            else
            {
                valid.Append(char.IsSurrogate(c) ? '�' : c);
            }
        }

        var normalized = valid.ToString().Normalize(NormalizationForm.FormKC);
        var result = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.Format)
            {
                continue;
            }

            if (category == UnicodeCategory.DecimalDigitNumber && !char.IsAsciiDigit(c))
            {
                result.Append((char)('0' + CharUnicodeInfo.GetDecimalDigitValue(c)));
            }
            else if (category == UnicodeCategory.SpaceSeparator)
            {
                result.Append(' ');
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
}
