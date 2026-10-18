namespace Conversia.Application.Metrics;

/// <summary>
/// The validated <c>Metrics</c> section (the time zone is <see cref="Time.TenantTimeZone"/>). Every default is explained in docs/METRICS.md.
/// </summary>
public sealed record MetricsOptions(
    int FirstResponseSlaMinutes,
    int ConversionMaturityDays,
    int TopLossReasons,
    int MinSampleSize,
    int ConversionDropAlertPoints,
    int BranchBelowAveragePoints)
{
    public const string SectionName = "Metrics";
    public const string FirstResponseSlaMinutesKey = SectionName + ":FirstResponseSlaMinutes";
    public const string ConversionMaturityDaysKey = SectionName + ":ConversionMaturityDays";
    public const string TopLossReasonsKey = SectionName + ":TopLossReasons";
    public const string MinSampleSizeKey = SectionName + ":MinSampleSize";
    public const string ConversionDropAlertPointsKey = SectionName + ":ConversionDropAlertPoints";
    public const string BranchBelowAveragePointsKey = SectionName + ":BranchBelowAveragePoints";

    /// <summary>The oldest range a request may ask for.</summary>
    public const int MaxRangeDays = 366;

    /// <summary>The range of a request that names only one end, or none.</summary>
    public const int DefaultRangeDays = 30;

    /// <summary>The most unanswered conversations the alerts list.</summary>
    public const int MaxUnansweredListed = 20;

    public static MetricsOptions Default { get; } = new(
        FirstResponseSlaMinutes: 30, ConversionMaturityDays: 7, TopLossReasons: 5, MinSampleSize: 20, ConversionDropAlertPoints: 10, BranchBelowAveragePoints: 15);

    public TimeSpan FirstResponseSla => TimeSpan.FromMinutes(FirstResponseSlaMinutes);
}
