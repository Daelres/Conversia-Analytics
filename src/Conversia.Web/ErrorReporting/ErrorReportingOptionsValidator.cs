using Microsoft.Extensions.Options;

namespace Conversia.Web.ErrorReporting;

/// <summary>Refuses an invalid <c>ErrorReporting</c> section at startup, naming the key and never printing the shared secret.</summary>
internal sealed class ErrorReportingOptionsValidator : IValidateOptions<ErrorReportingOptions>
{
    private const string Prefix = ErrorReportingOptions.SectionName + ":";

    public ValidateOptionsResult Validate(string? name, ErrorReportingOptions options)
    {
        var failures = new List<string>();

        if (options.TimeoutSeconds is < 1 or > ErrorReportingOptions.MaximumTimeoutSeconds)
        {
            failures.Add($"{Prefix}{nameof(options.TimeoutSeconds)} must be a whole number between 1 and {ErrorReportingOptions.MaximumTimeoutSeconds}.");
        }

        if (options.MaxPerFingerprintPerMinute < 1)
        {
            failures.Add($"{Prefix}{nameof(options.MaxPerFingerprintPerMinute)} must be a whole number of at least 1.");
        }

        if (options.CanReport && options.ApiKey!.Length < ErrorReportingOptions.MinimumKeyLength)
        {
            failures.Add($"{Prefix}{nameof(options.ApiKey)} must be at least {ErrorReportingOptions.MinimumKeyLength} characters long.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
