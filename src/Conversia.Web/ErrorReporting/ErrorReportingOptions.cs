namespace Conversia.Web.ErrorReporting;

/// <summary>The <c>ErrorReporting</c> configuration section: how the Web registers its own unexpected errors with the API.</summary>
public sealed class ErrorReportingOptions
{
    public const string SectionName = "ErrorReporting";

    public const int MinimumKeyLength = 32;

    public const int MaximumTimeoutSeconds = 30;

    /// <summary><c>false</c>: codes are still generated, shown and logged, but nothing is sent to the API.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The secret shared with the API (<c>ErrorLogging:ReportKey</c>). Blank: the Web does not report.</summary>
    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 2;

    public int MaxPerFingerprintPerMinute { get; set; } = 20;

    /// <summary>True when a report can actually be sent.</summary>
    public bool CanReport => Enabled && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Never prints the key, only whether there is one.</summary>
    public override string ToString()
        => $"ErrorReportingOptions {{ Enabled = {Enabled}, ApiKey = {(string.IsNullOrWhiteSpace(ApiKey) ? "(none)" : "(set)")}, TimeoutSeconds = {TimeoutSeconds}, MaxPerFingerprintPerMinute = {MaxPerFingerprintPerMinute} }}";
}
