using System.Text;

namespace Conversia.Application.Ingestion;

/// <summary>
/// Reads the channel secret out of a standard <c>Authorization: Basic base64(user:secret)</c> header, the alternative to
/// <c>X-Webhook-Secret</c> for a provider that forwards only the <c>Authorization</c> header. The secret is the Basic <i>password</i>; the
/// user name is ignored (any value, even empty, is accepted; the documented convention is <see cref="ConventionalUserName"/>).
/// Anything malformed is simply "no credential": the caller treats it as a failed authentication, never as an error, and the header value is
/// never echoed, logged or put in an exception.
/// </summary>
public static class WebhookBasicAuth
{
    /// <summary>The user name documented for the Basic variant. Not checked.</summary>
    public const string ConventionalUserName = "conversia";

    /// <summary>Longest <c>Authorization</c> value looked at; a longer one is refused before any decoding.</summary>
    public const int MaxHeaderLength = 1024;

    private const string Scheme = "Basic";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The Basic password, or null when the header is absent, not Basic, too long, not valid base64 / UTF-8, has no colon or has an empty password.</summary>
    public static string? TryReadPassword(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue) || headerValue.Length > MaxHeaderLength)
        {
            return null;
        }

        var value = headerValue.Trim();
        if (value.Length <= Scheme.Length
            || !value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            || !char.IsWhiteSpace(value[Scheme.Length]))
        {
            return null;
        }

        var token = value[Scheme.Length..].Trim();
        var buffer = new byte[token.Length]; // base64 decodes to fewer bytes than it has characters
        if (token.Length == 0 || !Convert.TryFromBase64String(token, buffer, out var written))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(buffer, 0, written);
        }
        catch (ArgumentException) // DecoderFallbackException: not UTF-8
        {
            return null;
        }

        var colon = decoded.IndexOf(':');
        return colon < 0 || colon == decoded.Length - 1 ? null : decoded[(colon + 1)..];
    }

    /// <summary>The header value a client sends for <paramref name="secret"/> (used by the simulator and the tests).</summary>
    public static string Header(string secret, string userName = ConventionalUserName)
        => $"{Scheme} {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{secret}"))}";
}
