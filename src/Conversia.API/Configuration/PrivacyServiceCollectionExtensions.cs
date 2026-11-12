using Conversia.Application.Privacy;

namespace Conversia.API.Configuration;

public static class PrivacyServiceCollectionExtensions
{
    public const string HashKeyKey = "Privacy:HashKey";
    public const string HashKeyIdKey = "Privacy:HashKeyId";
    public const string RetiredHashKeysSection = "Privacy:RetiredHashKeys";

    private const string Advice = "must be base64 of at least 32 random bytes (openssl rand -base64 32)";

    /// <summary>
    /// The privacy key that keys the hashes of suppressed numbers and deleted company names (docs/USERS_TENANTS_PERMISSIONS.md, "Contact
    /// suppression"). Its own secret, not <c>Classification:Llm:HashKey</c> (optional, and for another purpose) nor
    /// <c>Messaging:CredentialEncryptionKey</c> (optional too, and an encryption key): this one is required outside Development. Unset in
    /// Development: a fixed, non-secret key with a warning at startup. A malformed value stops the API naming the key, never the value.
    /// </summary>
    public static IServiceCollection AddPrivacy(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var keys = GetValidatedKeys(configuration, environment);
        services.AddSingleton(keys);
        services.AddSingleton<PrivacyHasher>();
        if (keys.IsDevelopmentFallback)
        {
            services.AddHostedService<PrivacyKeyFallbackWarning>();
        }

        return services;
    }

    public static PrivacyHashKeys GetValidatedKeys(IConfiguration configuration, IHostEnvironment environment)
    {
        var raw = configuration[HashKeyKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return environment.IsDevelopment()
                ? PrivacyHashKeys.DevelopmentFallback()
                : throw new InvalidOperationException($"{HashKeyKey} is required outside Development: base64 of at least 32 random bytes (openssl rand -base64 32). Set it via the Privacy__HashKey environment variable or Azure Key Vault.");
        }

        var id = configuration[HashKeyIdKey] is { Length: > 0 } configured ? configured.Trim() : "1";
        var retired = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var child in configuration.GetSection(RetiredHashKeysSection).GetChildren())
        {
            retired[child.Key] = Decode($"{RetiredHashKeysSection}:{child.Key}", child.Value);
        }

        try
        {
            return new PrivacyHashKeys(id, Decode(HashKeyKey, raw), retired);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"{HashKeyIdKey} and the ids under {RetiredHashKeysSection} are 1 to 32 letters, digits, '-' or '_', all different; every key {Advice}.");
        }
    }

    private static byte[] Decode(string key, string? value)
    {
        try
        {
            var bytes = Convert.FromBase64String((value ?? string.Empty).Trim());
            return bytes.Length >= PrivacyHashKeys.MinKeyBytes ? bytes : throw new InvalidOperationException($"{key} {Advice}.");
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{key} {Advice}.");
        }
    }

    private sealed class PrivacyKeyFallbackWarning(ILogger<PrivacyKeyFallbackWarning> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogWarning("{Key} is not set: the hashes of suppressed contacts and deleted companies use the fixed Development key, which protects nothing. Set it before any real data.", HashKeyKey);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
