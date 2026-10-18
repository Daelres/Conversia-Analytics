using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;

namespace Conversia.Domain.Common;

/// <summary>
/// The human-friendly code of a registered error: <c>ERR-yyyyMMdd-XXXXXX</c> (UTC date, then six characters of the
/// Crockford base32 alphabet <c>0-9 A-H J K M N P-T V-Z</c>).
/// </summary>
public static class ErrorCode
{
    private const string Prefix = "ERR-";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int DateLength = 8;
    private const int SuffixLength = 6;
    private const int BitsPerCharacter = 5;
    private const int Length = 4 + DateLength + 1 + SuffixLength;

    private static readonly SearchValues<char> AlphabetValues = SearchValues.Create(Alphabet);

    /// <summary>A new random code dated with the UTC date of <paramref name="utcNow"/>.</summary>
    public static string New(DateTime utcNow)
    {
        var date = (utcNow.Kind == DateTimeKind.Local ? utcNow.ToUniversalTime() : utcNow)
            .ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        // 32 symbols and 5 bits per character: every random bit pattern maps to a symbol, so no modulo bias and no rejection.
        Span<byte> random = stackalloc byte[4];
        RandomNumberGenerator.Fill(random);
        var bits = BitConverter.ToUInt32(random);

        return string.Create(Length, (date, bits), static (buffer, state) =>
        {
            Prefix.CopyTo(buffer);
            state.date.CopyTo(buffer[Prefix.Length..]);
            buffer[Prefix.Length + DateLength] = '-';
            var suffix = buffer[(Prefix.Length + DateLength + 1)..];
            for (var i = 0; i < SuffixLength; i++)
            {
                suffix[i] = Alphabet[(int)((state.bits >> (i * BitsPerCharacter)) & 31)];
            }
        });
    }

    /// <summary>True only for a string that is exactly one well-formed code.</summary>
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Length || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var date = value.AsSpan(Prefix.Length, DateLength);
        var suffix = value.AsSpan(Prefix.Length + DateLength + 1);
        return value[Prefix.Length + DateLength] == '-'
            && !date.ContainsAnyExceptInRange('0', '9')
            && !suffix.ContainsAnyExcept(AlphabetValues);
    }
}
