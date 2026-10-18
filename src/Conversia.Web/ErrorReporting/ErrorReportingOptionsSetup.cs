using System.Globalization;
using Microsoft.Extensions.Options;

namespace Conversia.Web.ErrorReporting;

/// <summary>
/// Reads the <c>ErrorReporting</c> section. A blank value means "use the default" (an empty environment variable is how a compose
/// file leaves a setting unset), which the configuration binder would refuse; a value of the wrong type names its key.
/// </summary>
internal sealed class ErrorReportingOptionsSetup(IConfiguration configuration) : IConfigureOptions<ErrorReportingOptions>
{
    public void Configure(ErrorReportingOptions options)
    {
        var section = configuration.GetSection(ErrorReportingOptions.SectionName);
        options.Enabled = Read(section, nameof(options.Enabled), options.Enabled, bool.TryParse, "'true' or 'false'");
        options.TimeoutSeconds = Read(section, nameof(options.TimeoutSeconds), options.TimeoutSeconds, ParseInt, "a whole number");
        options.MaxPerFingerprintPerMinute = Read(section, nameof(options.MaxPerFingerprintPerMinute), options.MaxPerFingerprintPerMinute, ParseInt, "a whole number");
        options.ApiKey = section[nameof(options.ApiKey)];
    }

    private delegate bool TryParse<T>(string text, out T value);

    private static bool ParseInt(string text, out int value) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static T Read<T>(IConfigurationSection section, string name, T fallback, TryParse<T> tryParse, string expected)
    {
        var raw = section[name];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return tryParse(raw.Trim(), out var value)
            ? value
            : throw new InvalidOperationException($"{ErrorReportingOptions.SectionName}:{name} must be {expected}.");
    }
}
