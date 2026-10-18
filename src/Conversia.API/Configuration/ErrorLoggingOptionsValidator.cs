using System.Globalization;
using Conversia.Infrastructure.Configuration;

namespace Conversia.API.Configuration;

/// <summary>Fails fast at startup (<see cref="InvalidOperationException"/> naming the key, never the report key) on an invalid <c>ErrorLogging</c> section.</summary>
public static class ErrorLoggingOptionsValidator
{
    public const int MinReportKeyLength = 32;

    private const int MaxRetentionDays = 3650;
    private const int MaxPersistTimeoutSeconds = 30;

    public static ErrorLoggingOptions GetValidatedOptions(IConfiguration configuration)
    {
        var defaults = ErrorLoggingOptions.Default;

        return new ErrorLoggingOptions(
            Enabled: ReadBool(configuration, ErrorLoggingOptions.EnabledKey, defaults.Enabled),
            RetentionDays: ReadInt(configuration, ErrorLoggingOptions.RetentionDaysKey, defaults.RetentionDays, 1, MaxRetentionDays),
            MaxPerFingerprintPerMinute: ReadInt(configuration, ErrorLoggingOptions.MaxPerFingerprintPerMinuteKey, defaults.MaxPerFingerprintPerMinute, 1, int.MaxValue),
            PersistTimeoutSeconds: ReadInt(configuration, ErrorLoggingOptions.PersistTimeoutSecondsKey, defaults.PersistTimeoutSeconds, 1, MaxPersistTimeoutSeconds),
            ReportRateLimitPerMinute: ReadInt(configuration, ErrorLoggingOptions.ReportRateLimitPerMinuteKey, defaults.ReportRateLimitPerMinute, 1, int.MaxValue),
            ReportKey: ReadReportKey(configuration));
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

    private static string? ReadReportKey(IConfiguration configuration)
    {
        var key = configuration[ErrorLoggingOptions.ReportKeyKey];
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return key.Length >= MinReportKeyLength
            ? key
            : throw new InvalidOperationException($"{ErrorLoggingOptions.ReportKeyKey} must be at least {MinReportKeyLength} characters long.");
    }
}
