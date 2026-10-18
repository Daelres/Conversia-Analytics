using System.Globalization;
using Conversia.Infrastructure.Configuration;

namespace Conversia.API.Configuration;

/// <summary>The validated <c>Webhooks</c> section of the endpoint.</summary>
public sealed record WebhookEndpointOptions(int MaxBodyBytes, int RateLimitPerMinute)
{
    public const string SectionName = "Webhooks";
    public const string MaxBodyBytesKey = SectionName + ":MaxBodyBytes";
    public const string RateLimitPerMinuteKey = SectionName + ":RateLimitPerMinute";

    public static WebhookEndpointOptions Default { get; } = new(MaxBodyBytes: 1_048_576, RateLimitPerMinute: 600);
}

/// <summary>Fails fast at startup (<see cref="InvalidOperationException"/> naming the key) on an invalid <c>Webhooks</c> section.</summary>
public static class WebhookOptionsValidator
{
    public static WebhookEndpointOptions GetValidatedEndpointOptions(IConfiguration configuration)
    {
        var defaults = WebhookEndpointOptions.Default;
        return new WebhookEndpointOptions(
            MaxBodyBytes: ReadInt(configuration, WebhookEndpointOptions.MaxBodyBytesKey, defaults.MaxBodyBytes, 1024, 16 * 1024 * 1024),
            RateLimitPerMinute: ReadInt(configuration, WebhookEndpointOptions.RateLimitPerMinuteKey, defaults.RateLimitPerMinute, 1, int.MaxValue));
    }

    public static WebhookProcessingOptions GetValidatedProcessingOptions(IConfiguration configuration)
    {
        var defaults = WebhookProcessingOptions.Default;
        return new WebhookProcessingOptions(
            Enabled: ReadBool(configuration, WebhookProcessingOptions.EnabledKey, defaults.Enabled),
            PollIntervalMilliseconds: ReadInt(configuration, WebhookProcessingOptions.PollIntervalMillisecondsKey, defaults.PollIntervalMilliseconds, 10, 600_000),
            BatchSize: ReadInt(configuration, WebhookProcessingOptions.BatchSizeKey, defaults.BatchSize, 1, 1000),
            MaxAttempts: ReadInt(configuration, WebhookProcessingOptions.MaxAttemptsKey, defaults.MaxAttempts, 1, 100),
            RetryBaseDelaySeconds: ReadInt(configuration, WebhookProcessingOptions.RetryBaseDelaySecondsKey, defaults.RetryBaseDelaySeconds, 0, 86_400));
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool defaultValue)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return bool.TryParse(raw, out var value) ? value : throw new InvalidOperationException($"{key} must be 'true' or 'false'.");
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue, int min, int max)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            var range = max == int.MaxValue ? $"at least {min}" : $"between {min} and {max}";
            throw new InvalidOperationException($"{key} must be a whole number {range}.");
        }

        return value;
    }
}
