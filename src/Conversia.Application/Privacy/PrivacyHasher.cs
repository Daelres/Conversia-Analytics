using System.Security.Cryptography;
using System.Text;
using Conversia.Domain.Common;

namespace Conversia.Application.Privacy;

/// <summary>A keyed hash and the id of the key that made it.</summary>
public readonly record struct KeyedHash(string KeyId, string Hex);

/// <summary>
/// <c>Privacy:*</c>: the secret that keys the hashes the platform keeps of personal data it must still recognise (a suppressed number, the name
/// of a deleted company). <see cref="Current"/> makes every new hash; <see cref="Retired"/> keys are only used to recognise old ones.
/// </summary>
public sealed class PrivacyHashKeys
{
    public const int MinKeyBytes = 32;

    public PrivacyHashKeys(string currentId, byte[] current, IReadOnlyDictionary<string, byte[]>? retired = null, bool isDevelopmentFallback = false)
    {
        CurrentId = Validate(currentId, current);
        Current = current;
        Retired = retired ?? new Dictionary<string, byte[]>();
        foreach (var (id, key) in Retired)
        {
            Validate(id, key);
            if (id == currentId)
            {
                throw new ArgumentException("A retired privacy key cannot have the id of the current one.", nameof(retired));
            }
        }

        IsDevelopmentFallback = isDevelopmentFallback;
    }

    public string CurrentId { get; }

    public byte[] Current { get; }

    public IReadOnlyDictionary<string, byte[]> Retired { get; }

    /// <summary>True when no key was configured and the fixed Development key is in use (never outside Development).</summary>
    public bool IsDevelopmentFallback { get; }

    /// <summary>The fixed key Development uses when <c>Privacy:HashKey</c> is unset: it is not a secret, so its hashes protect nothing.</summary>
    public static PrivacyHashKeys DevelopmentFallback()
        => new("dev", SHA256.HashData(Encoding.UTF8.GetBytes("conversia-development-privacy-hash-key-not-a-secret")), isDevelopmentFallback: true);

    private static string Validate(string id, byte[] key)
    {
        if (!KeyedHashes.IsKeyId(id))
        {
            throw new ArgumentException("A privacy key id is 1 to 32 letters, digits, '-' or '_'.", nameof(id));
        }

        return key is { Length: >= MinKeyBytes } ? id : throw new ArgumentException($"A privacy key has at least {MinKeyBytes} bytes.", nameof(key));
    }
}

/// <summary>
/// HMAC-SHA256 with the deployment's privacy key: without the key a stored hash cannot be confirmed by trying every phone number. The purpose
/// is part of the input, so a hash made for one use never matches one made for another.
/// </summary>
public sealed class PrivacyHasher(PrivacyHashKeys keys)
{
    public const string ContactSuppressionPurpose = "contact-suppression";
    public const string TenantNamePurpose = "tenant-name";

    public string CurrentKeyId => keys.CurrentId;

    public KeyedHash Hash(string purpose, string value) => new(keys.CurrentId, Hmac(keys.Current, purpose, value));

    /// <summary>The hash under the current key and under every retired one: what a lookup must accept after a key rotation.</summary>
    public IReadOnlyList<KeyedHash> Candidates(string purpose, string value)
        => [Hash(purpose, value), .. keys.Retired.Select(r => new KeyedHash(r.Key, Hmac(r.Value, purpose, value)))];

    public KeyedHash ContactSuppression(Guid tenantId, string waId) => Hash(ContactSuppressionPurpose, ContactValue(tenantId, waId));

    public IReadOnlyList<KeyedHash> ContactSuppressionCandidates(Guid tenantId, string waId) => Candidates(ContactSuppressionPurpose, ContactValue(tenantId, waId));

    public KeyedHash TenantName(string name) => Hash(TenantNamePurpose, name.Trim().ToUpperInvariant());

    private static string ContactValue(Guid tenantId, string waId) => $"{tenantId:N}:{waId}";

    private static string Hmac(byte[] key, string purpose, string value)
        => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(purpose + "\n" + value)));
}
