using Conversia.Domain.Enums;

namespace Conversia.Infrastructure.Configuration;

/// <summary>
/// The master key that encrypts the secrets stored in the database (<c>Messaging:CredentialEncryptionKey</c>) and the id written next to
/// every value it encrypts (<c>Messaging:CredentialEncryptionKeyId</c>). A class with its own <c>ToString</c>, never a record, so the
/// key cannot be printed by accident.
/// </summary>
public sealed class SecretProtectionOptions
{
    public const string SectionName = "Messaging";
    public const string KeyKey = SectionName + ":CredentialEncryptionKey";
    public const string KeyIdKey = SectionName + ":CredentialEncryptionKeyId";
    public const int KeyBytes = 32;
    public const int KeyIdMaxLength = 32;
    public const string DefaultKeyId = "1";

    public SecretProtectionOptions(byte[]? key, string keyId = DefaultKeyId)
    {
        if (key is not null && key.Length != KeyBytes)
        {
            throw new ArgumentException($"The key must be {KeyBytes} bytes.", nameof(key));
        }

        if (!IsValidKeyId(keyId))
        {
            throw new ArgumentException($"The key id must be 1 to {KeyIdMaxLength} letters, digits, '-' or '_'.", nameof(keyId));
        }

        Key = key;
        KeyId = keyId;
    }

    /// <summary>No key: the API runs, and whatever needs a stored secret answers that it is unavailable.</summary>
    public static SecretProtectionOptions NotConfigured { get; } = new(null);

    /// <summary>The 32 bytes of the AES-256 key; null when none is configured.</summary>
    public byte[]? Key { get; }

    /// <summary>Written in front of every value encrypted with <see cref="Key"/>, so a later key can be told apart from this one.</summary>
    public string KeyId { get; }

    public static bool IsValidKeyId(string? keyId)
        => keyId is { Length: >= 1 and <= KeyIdMaxLength } && keyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public override string ToString() => $"SecretProtectionOptions {{ Configured = {Key is not null}, KeyId = {KeyId} }}";
}

/// <summary>
/// The validated outbound part of the <c>Dialog360</c> section: where each provider environment lives and how long a call may take. The
/// base URLs come from configuration only; a request chooses an environment, never a URL.
/// </summary>
public sealed record Dialog360ApiOptions(Uri SandboxBaseUrl, Uri ProductionBaseUrl, int TimeoutSeconds)
{
    public const string SectionName = "Dialog360";
    public const string SandboxBaseUrlKey = SectionName + ":SandboxBaseUrl";
    public const string ProductionBaseUrlKey = SectionName + ":ProductionBaseUrl";
    public const string TimeoutSecondsKey = SectionName + ":TimeoutSeconds";

    public static Dialog360ApiOptions Default { get; } = new(
        SandboxBaseUrl: new Uri("https://waba-sandbox.360dialog.io"), ProductionBaseUrl: new Uri("https://waba-v2.360dialog.io"), TimeoutSeconds: 15);

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    public Uri BaseUrlOf(WhatsAppProviderEnvironment environment) => environment switch
    {
        WhatsAppProviderEnvironment.Sandbox => SandboxBaseUrl,
        WhatsAppProviderEnvironment.Production => ProductionBaseUrl,
        _ => throw new ArgumentOutOfRangeException(nameof(environment)),
    };
}

/// <summary>
/// The validated limits of sending under <c>Messaging</c>: messages per minute for one user and for a whole company, and how long the
/// transaction that records a sent message may take (each statement, and each lock wait).
/// </summary>
public sealed record MessageSendingOptions(int SendRateLimitPerMinute, int TenantSendRateLimitPerMinute, int RecordTimeoutSeconds)
{
    public const string SendRateLimitPerMinuteKey = SecretProtectionOptions.SectionName + ":SendRateLimitPerMinute";
    public const string TenantSendRateLimitPerMinuteKey = SecretProtectionOptions.SectionName + ":TenantSendRateLimitPerMinute";
    public const string RecordTimeoutSecondsKey = SecretProtectionOptions.SectionName + ":RecordTimeoutSeconds";

    public static MessageSendingOptions Default { get; } = new(SendRateLimitPerMinute: 30, TenantSendRateLimitPerMinute: 120, RecordTimeoutSeconds: 30);

    public TimeSpan RecordTimeout => TimeSpan.FromSeconds(RecordTimeoutSeconds);
}
