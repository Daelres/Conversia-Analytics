using System.Buffers.Text;
using System.Security.Cryptography;

namespace Conversia.Application.Reports;

/// <summary>
/// The token of an unsubscribe link (docs/REPORTS.md, "Unsubscribe"): the recipient row's id and an HMAC-SHA256 tag of it (16 bytes),
/// base64url. It names one person in one report definition and nothing else (no address), never expires (an old email must still work) and
/// cannot be forged without the key. Comparing tags takes constant time.
/// </summary>
public static class ReportUnsubscribeToken
{
    private const int TagLength = 16;
    private const int TokenLength = 43; // base64url of 32 bytes, no padding
    private static readonly byte[] Purpose = "conversia-report-unsubscribe-v1"u8.ToArray();

    public static string Create(Guid recipientId, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var id = recipientId.ToByteArray();
        return Base64Url.EncodeToString([.. id, .. Tag(id, key)]);
    }

    public static bool TryRead(string? token, byte[] key, out Guid recipientId)
    {
        ArgumentNullException.ThrowIfNull(key);
        recipientId = Guid.Empty;
        if (token is not { Length: TokenLength } || !Base64Url.IsValid(token))
        {
            return false;
        }

        var bytes = Base64Url.DecodeFromChars(token);
        if (bytes.Length != 16 + TagLength)
        {
            return false;
        }

        var id = bytes[..16];
        if (!CryptographicOperations.FixedTimeEquals(bytes[16..], Tag(id, key)))
        {
            return false;
        }

        recipientId = new Guid(id);
        return true;
    }

    /// <summary>The key of the tokens, derived from a deployment secret (HKDF-SHA256), so no new secret has to be configured.</summary>
    public static byte[] DeriveKey(byte[] secret)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt: null, info: Purpose);

    private static byte[] Tag(byte[] id, byte[] key) => HMACSHA256.HashData(key, (byte[])[.. Purpose, .. id])[..TagLength];
}
