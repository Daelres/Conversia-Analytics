using System.Security.Cryptography;
using System.Text;

namespace Conversia.Application.ErrorLogging;

/// <summary>
/// Groups repeated occurrences of one defect: the first 16 lowercase hex characters of SHA-256 over
/// <c>ExceptionType + "|" + topApplicationFrame</c>.
/// </summary>
/// <remarks>
/// The top application frame is the first <c>Conversia.</c> frame of the stack text (or the first frame when there is none),
/// as <c>Namespace.Type.Method</c>: arguments, offsets, file paths, line numbers, generic arity and compiler-generated
/// names are left out, so a redeploy or a different message does not split one defect into several.
/// </remarks>
public static class ErrorFingerprint
{
    private const string ApplicationPrefix = "Conversia.";
    private const string FramePrefix = "at ";
    private const int HexLength = 16;

    /// <param name="exceptionType">Full type name of the exception.</param>
    /// <param name="exceptionDetails">The full <c>exception.ToString()</c> text (may be null or empty).</param>
    public static string Compute(string exceptionType, string? exceptionDetails)
    {
        var source = string.Concat(exceptionType, "|", TopApplicationFrame(exceptionDetails));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexStringLower(hash)[..HexLength];
    }

    private static string TopApplicationFrame(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return string.Empty;
        }

        string? first = null;
        foreach (var line in details.AsSpan().EnumerateLines())
        {
            var name = FrameName(line);
            if (name.IsEmpty)
            {
                continue;
            }

            var frame = StackFrameName.Normalize(name);
            if (frame.StartsWith(ApplicationPrefix, StringComparison.Ordinal))
            {
                return frame;
            }

            first ??= frame;
        }

        return first ?? string.Empty;
    }

    // "   at Ns.Type.Method(args) in file:line 1" -> "Ns.Type.Method"; anything else -> empty.
    private static ReadOnlySpan<char> FrameName(ReadOnlySpan<char> line)
    {
        var text = line.TrimStart(" \t");
        if (!text.StartsWith(FramePrefix, StringComparison.Ordinal))
        {
            return default;
        }

        var name = text[FramePrefix.Length..].TrimStart(" \t");
        var end = name.IndexOfAny(" \t(");
        return end < 0 ? name : name[..end];
    }
}
