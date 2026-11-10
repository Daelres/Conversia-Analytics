namespace Conversia.Application.Interfaces;

/// <summary>
/// Protects a secret that must be stored and read back (unlike a password or a webhook secret, which are only hashed). The stored form
/// is opaque text. Both operations throw <see cref="Exceptions.SecretProtectionException"/>, never anything that describes the secret.
/// </summary>
public interface ISecretProtector
{
    /// <summary>False when no master key is configured: every call then fails with <see cref="Exceptions.SecretProtectionFailure.NotConfigured"/>.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Returns the stored form of <paramref name="plaintext"/>, bound to <paramref name="purpose"/> (what the secret belongs to): it can be
    /// read back only with the same purpose. Two calls with the same input return different values.
    /// </summary>
    string Protect(string plaintext, string purpose);

    /// <summary>The secret behind a value produced by <see cref="Protect"/> with the same <paramref name="purpose"/> and master key.</summary>
    string Unprotect(string protectedValue, string purpose);
}
