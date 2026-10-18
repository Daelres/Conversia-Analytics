using System.Text;

namespace Conversia.Infrastructure.Configuration;

/// <summary>The validated <c>ErrorLogging</c> section.</summary>
public sealed record ErrorLoggingOptions(
    bool Enabled,
    int RetentionDays,
    int MaxPerFingerprintPerMinute,
    int PersistTimeoutSeconds,
    int ReportRateLimitPerMinute,
    string? ReportKey)
{
    public const string SectionName = "ErrorLogging";
    public const string EnabledKey = SectionName + ":Enabled";
    public const string RetentionDaysKey = SectionName + ":RetentionDays";
    public const string MaxPerFingerprintPerMinuteKey = SectionName + ":MaxPerFingerprintPerMinute";
    public const string PersistTimeoutSecondsKey = SectionName + ":PersistTimeoutSeconds";
    public const string ReportRateLimitPerMinuteKey = SectionName + ":ReportRateLimitPerMinute";
    public const string ReportKeyKey = SectionName + ":ReportKey";

    /// <summary>Whether the report endpoint is on: it is, exactly when a report key is configured.</summary>
    public bool ReportEndpointEnabled => !string.IsNullOrEmpty(ReportKey);

    public static ErrorLoggingOptions Default { get; } = new(
        Enabled: true,
        RetentionDays: 90,
        MaxPerFingerprintPerMinute: 20,
        PersistTimeoutSeconds: 3,
        ReportRateLimitPerMinute: 60,
        ReportKey: null);

    // The synthesized ToString would print every member, the report key included.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Enabled = {Enabled}, RetentionDays = {RetentionDays}, MaxPerFingerprintPerMinute = {MaxPerFingerprintPerMinute}, ")
            .Append($"PersistTimeoutSeconds = {PersistTimeoutSeconds}, ReportRateLimitPerMinute = {ReportRateLimitPerMinute}, ")
            .Append($"ReportEndpointEnabled = {ReportEndpointEnabled}");
        return true;
    }
}
