using Conversia.Infrastructure.Configuration;

namespace Conversia.API.Configuration;

/// <summary>
/// Fails fast at startup (<see cref="InvalidOperationException"/> naming the key, never its value) on an invalid messaging setting.
/// <para>
/// <c>Messaging:CredentialEncryptionKey</c> is the one secret here and it is <b>optional</b>: an installation that only ingests does not
/// need it, so its absence must not stop the API (nor the migrations bundle, which boots this host). Without it the API runs and the
/// endpoints that need a stored provider key answer 503 <c>provider_credentials_unavailable</c>. A value that is present but malformed is
/// a deployment mistake and stops the API, the same way a bad <c>Jwt:SigningKey</c> does.
/// </para>
/// </summary>
public static class MessagingOptionsValidator
{
    public static SecretProtectionOptions GetValidatedSecretProtectionOptions(IConfiguration configuration)
    {
        var keyId = configuration[SecretProtectionOptions.KeyIdKey];
        keyId = string.IsNullOrWhiteSpace(keyId) ? SecretProtectionOptions.DefaultKeyId : keyId.Trim();
        if (!SecretProtectionOptions.IsValidKeyId(keyId))
        {
            throw new InvalidOperationException(
                $"{SecretProtectionOptions.KeyIdKey} must be 1 to {SecretProtectionOptions.KeyIdMaxLength} letters, digits, '-' or '_'.");
        }

        var raw = configuration[SecretProtectionOptions.KeyKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new SecretProtectionOptions(null, keyId);
        }

        var key = new byte[SecretProtectionOptions.KeyBytes + 1];
        if (!Convert.TryFromBase64String(raw.Trim(), key, out var written) || written != SecretProtectionOptions.KeyBytes)
        {
            throw Malformed($"must be the base64 of exactly {SecretProtectionOptions.KeyBytes} random bytes");
        }

        key = key[..SecretProtectionOptions.KeyBytes];
        if (key.Distinct().Count() == 1)
        {
            throw Malformed("is not random (every byte is the same)");
        }

        return new SecretProtectionOptions(key, keyId);
    }

    public static Dialog360ApiOptions GetValidatedDialog360ApiOptions(IConfiguration configuration)
    {
        var defaults = Dialog360ApiOptions.Default;
        return new Dialog360ApiOptions(
            SandboxBaseUrl: ReadBaseUrl(configuration, Dialog360ApiOptions.SandboxBaseUrlKey, defaults.SandboxBaseUrl),
            ProductionBaseUrl: ReadBaseUrl(configuration, Dialog360ApiOptions.ProductionBaseUrlKey, defaults.ProductionBaseUrl),
            TimeoutSeconds: ConfigurationReader.ReadInt(configuration, Dialog360ApiOptions.TimeoutSecondsKey, defaults.TimeoutSeconds, 1, 120));
    }

    public static MessageSendingOptions GetValidatedSendingOptions(IConfiguration configuration)
    {
        var defaults = MessageSendingOptions.Default;
        return new MessageSendingOptions(
            SendRateLimitPerMinute: ConfigurationReader.ReadInt(configuration, MessageSendingOptions.SendRateLimitPerMinuteKey, defaults.SendRateLimitPerMinute, 1, 100_000),
            TenantSendRateLimitPerMinute: ConfigurationReader.ReadInt(configuration, MessageSendingOptions.TenantSendRateLimitPerMinuteKey, defaults.TenantSendRateLimitPerMinute, 1, 1_000_000),
            RecordTimeoutSeconds: ConfigurationReader.ReadInt(configuration, MessageSendingOptions.RecordTimeoutSecondsKey, defaults.RecordTimeoutSeconds, 1, 300));
    }

    // The channel's API key travels to this address: https only (http is accepted for a loopback address, for a local stand-in), and
    // nothing in the URL that could carry credentials or be mistaken for part of the path the adapter appends.
    private static Uri ReadBaseUrl(IConfiguration configuration, string key, Uri defaultValue)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url)
            || !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
            || !string.IsNullOrEmpty(url.UserInfo)
            || !string.IsNullOrEmpty(url.Query)
            || !string.IsNullOrEmpty(url.Fragment))
        {
            throw new InvalidOperationException(
                $"{key} must be an absolute https URL without user, query or fragment (http is accepted only for a loopback address).");
        }

        return url;
    }

    private static InvalidOperationException Malformed(string reason) =>
        new($"{SecretProtectionOptions.KeyKey} {reason}. Generate one with 'openssl rand -base64 32' and set it via 'dotnet user-secrets' or the Messaging__CredentialEncryptionKey environment variable; leave it unset to run without stored provider credentials.");
}
