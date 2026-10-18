using System.Globalization;

namespace Conversia.API.Configuration;

/// <summary>
/// Fails fast at startup when <c>RateLimiting:Login</c> holds a value that is not a positive integer.
/// An absent key falls back to <see cref="LoginRateLimitOptions.Default"/>.
/// </summary>
internal static class LoginRateLimitOptionsValidator
{
    public static LoginRateLimitOptions GetValidatedOptions(IConfiguration configuration)
    {
        var defaults = LoginRateLimitOptions.Default;

        return new LoginRateLimitOptions(
            ReadPositiveInt(configuration, LoginRateLimitOptions.PermitLimitKey, defaults.PermitLimit),
            ReadPositiveInt(configuration, LoginRateLimitOptions.WindowSecondsKey, defaults.WindowSeconds));
    }

    private static int ReadPositiveInt(IConfiguration configuration, string key, int defaultValue)
    {
        var raw = configuration[key];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new InvalidOperationException($"{key} must be a whole number of at least 1.");
        }

        return value;
    }
}
