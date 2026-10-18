using System.Text;

namespace Conversia.API.Configuration;

/// <summary>
/// Fails fast at startup when <c>Jwt:SigningKey</c> is missing or unsafe.
/// Error messages never contain the key value.
/// </summary>
internal static class JwtSigningKeyValidator
{
    public const string ConfigurationKey = "Jwt:SigningKey";
    public const int MinimumKeyBytes = 32;
    public const string ShippedPlaceholder = "REPLACE_WITH_A_STRONG_SECRET_AT_LEAST_32_BYTES_LONG";

    public static string GetValidatedSigningKey(IConfiguration configuration)
    {
        var signingKey = configuration[ConfigurationKey];

        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw Unsafe("is missing or blank");
        }

        if (signingKey.Trim() == ShippedPlaceholder)
        {
            throw Unsafe("is still the placeholder value from appsettings.json");
        }

        if (Encoding.UTF8.GetByteCount(signingKey) < MinimumKeyBytes)
        {
            throw Unsafe($"must be at least {MinimumKeyBytes} bytes (UTF-8)");
        }

        return signingKey;
    }

    private static InvalidOperationException Unsafe(string reason) =>
        new($"{ConfigurationKey} {reason}. Set it via 'dotnet user-secrets' or the Jwt__SigningKey environment variable.");
}
