using System.Diagnostics.CodeAnalysis;

namespace Conversia.Application.Validation;

/// <summary>
/// Base64 decoding for opaque tokens such as a row version. Unlike <see cref="Convert.TryFromBase64String(string, Span{byte}, out int)"/>
/// it rejects blank input and embedded whitespace, so the same text always means the same bytes.
/// </summary>
public static class StrictBase64
{
    public static bool TryDecode([NotNullWhen(true)] string? text, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;
        if (string.IsNullOrEmpty(text) || text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var buffer = new byte[text.Length];
        if (!Convert.TryFromBase64String(text, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];
        return true;
    }
}
