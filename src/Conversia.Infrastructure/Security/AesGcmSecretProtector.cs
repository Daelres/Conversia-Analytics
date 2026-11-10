using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Configuration;

namespace Conversia.Infrastructure.Security;

/// <summary>
/// AES-256-GCM with the master key of <see cref="SecretProtectionOptions"/>. The stored form is
/// <c>v1.{keyId}.{base64url(nonce || ciphertext || tag)}</c>: a random 96-bit nonce per value, a 128-bit tag, and the purpose as
/// associated data (authenticated, not stored), so a value is readable only for what it was written for. The version and the key id
/// in front are what a future key rotation needs to tell old values from new ones; today a value under another key id is unreadable.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[]? _key;
    private readonly string _keyId;

    public AesGcmSecretProtector(SecretProtectionOptions options)
    {
        _key = options.Key;
        _keyId = options.KeyId;
    }

    public bool IsConfigured => _key is not null;

    public string Protect(string plaintext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var key = _key ?? throw new SecretProtectionException(SecretProtectionFailure.NotConfigured);

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var payload = new byte[NonceBytes + plain.Length + TagBytes];
        var nonce = payload.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, payload.AsSpan(NonceBytes, plain.Length), payload.AsSpan(NonceBytes + plain.Length), Encoding.UTF8.GetBytes(purpose));
        CryptographicOperations.ZeroMemory(plain);

        return $"{Version}.{_keyId}.{Base64Url.EncodeToString(payload)}";
    }

    public string Unprotect(string protectedValue, string purpose)
    {
        var key = _key ?? throw new SecretProtectionException(SecretProtectionFailure.NotConfigured);

        // Every way of being unreadable is the same failure: nothing here tells an altered value from one written with another key.
        var parts = protectedValue?.Split('.') ?? [];
        if (parts.Length != 3 || parts[0] != Version || !string.Equals(parts[1], _keyId, StringComparison.Ordinal))
        {
            throw new SecretProtectionException(SecretProtectionFailure.Unreadable);
        }

        try
        {
            var payload = Base64Url.DecodeFromChars(parts[2]);
            if (payload.Length < NonceBytes + TagBytes)
            {
                throw new SecretProtectionException(SecretProtectionFailure.Unreadable);
            }

            var cipherLength = payload.Length - NonceBytes - TagBytes;
            var plain = new byte[cipherLength];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(
                payload.AsSpan(0, NonceBytes), payload.AsSpan(NonceBytes, cipherLength), payload.AsSpan(NonceBytes + cipherLength), plain,
                Encoding.UTF8.GetBytes(purpose));

            var secret = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return secret;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // No inner exception: nothing about the value or the key travels with the failure.
            throw new SecretProtectionException(SecretProtectionFailure.Unreadable);
        }
    }
}
