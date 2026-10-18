using System.Buffers.Text;
using System.Text.Json;

namespace Conversia.Web.Authentication;

/// <summary>
/// Reads the <c>exp</c> claim only, so the auth cookie can be bound to the API token's lifetime.
/// The signature is not checked: the API alone decides whether a token is valid.
/// </summary>
internal static class JwtExpiry
{
    public static bool TryRead(string token, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        try
        {
            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            if (payload.RootElement.ValueKind != JsonValueKind.Object
                || !payload.RootElement.TryGetProperty("exp", out var exp)
                || !exp.TryGetInt64(out var seconds))
            {
                return false;
            }

            expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return false;
        }
    }
}
