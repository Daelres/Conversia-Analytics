using System.Globalization;

namespace Conversia.API.Configuration;

/// <summary>
/// Reads a setting that is optional but, when present, must be valid: a bad value stops the API at startup with an
/// <see cref="InvalidOperationException"/> that names the key (never prints its value), instead of failing later in a worker.
/// </summary>
internal static class ConfigurationReader
{
    public static bool ReadBool(IConfiguration configuration, string key, bool defaultValue)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return bool.TryParse(raw, out var value) ? value : throw new InvalidOperationException($"{key} must be 'true' or 'false'.");
    }

    public static int ReadInt(IConfiguration configuration, string key, int defaultValue, int min, int max)
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
