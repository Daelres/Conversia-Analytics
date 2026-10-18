using System.Security.Cryptography;
using System.Text;

namespace Conversia.API.ErrorLogging;

/// <summary>
/// Checks the key the Web presents against the configured one in constant time: both sides are hashed first, so the
/// comparison is over two values of the same length whatever the caller sent and takes the same time wherever they differ.
/// </summary>
public sealed class ErrorReportKeyVerifier
{
    private readonly byte[]? _expectedHash;

    public ErrorReportKeyVerifier(string? configuredKey)
        => _expectedHash = string.IsNullOrEmpty(configuredKey) ? null : Hash(configuredKey);

    /// <summary>False when no key is configured: the report endpoint is then off.</summary>
    public bool IsEnabled => _expectedHash is not null;

    public bool Matches(string? presentedKey)
        => _expectedHash is not null
            && !string.IsNullOrEmpty(presentedKey)
            && CryptographicOperations.FixedTimeEquals(_expectedHash, Hash(presentedKey));

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
