namespace Conversia.Application.Reports;

/// <summary>
/// The validated <c>Reports</c> section (docs/REPORTS.md, "Configuration"). Holds no secret: the unsubscribe key is derived at startup from the
/// JWT signing key and kept apart (<see cref="ReportUnsubscribeKey"/>).
/// </summary>
/// <param name="WebBaseUrl">The public address of the Web, for the links of the emails and WhatsApp notices.</param>
/// <param name="CatchUpHours">How late a report may still be generated after its hour (an outage longer than this skips the period).</param>
/// <param name="MonthlyLlmReportsPerCompany">Reports a company may have written by the model per month; the rest use the template.</param>
/// <param name="MinConversionSample">Decided conversations a window needs for its conversion rate to be stated (<c>Reports:MinConversionSample</c>).</param>
/// <param name="DeploymentMonthlyUsdCap">Estimated USD the whole deployment may spend on reports a month.</param>
public sealed record ReportSettings(
    bool JobEnabled,
    int JobIntervalSeconds,
    int MaxDefinitionsPerCompany,
    int MaxRecipientsPerDefinition,
    int RetentionDays,
    int CatchUpHours,
    int MaxDeliveryAttempts,
    int PreviewsPerHour,
    bool AiEnabled,
    int MonthlyLlmReportsPerCompany,
    decimal DeploymentMonthlyUsdCap,
    Uri WebBaseUrl,
    int MinConversionSample = 20,
    int PreviewsPerCompanyPerDay = 10,
    int UnsubscribePerTokenPerMinute = 10,
    int UnsubscribeJunkPerMinute = 20)
{
    public const string SectionName = "Reports";

    public static ReportSettings Default { get; } = new(
        JobEnabled: true, JobIntervalSeconds: 60, MaxDefinitionsPerCompany: 5, MaxRecipientsPerDefinition: 10, RetentionDays: 180, CatchUpHours: 24,
        MaxDeliveryAttempts: 5, PreviewsPerHour: 6, AiEnabled: true, MonthlyLlmReportsPerCompany: 62, DeploymentMonthlyUsdCap: 5m,
        WebBaseUrl: new Uri("http://localhost:5112/"));

    /// <summary>Waits between delivery attempts: 1 minute, 5 minutes, 30 minutes, 2 hours, then 6 hours.</summary>
    public static TimeSpan Backoff(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(30),
        4 => TimeSpan.FromHours(2),
        _ => TimeSpan.FromHours(6),
    };

    public string ReportUrl(Guid reportId) => new Uri(WebBaseUrl, $"informes/{reportId:D}").ToString();

    public string UnsubscribeUrl(string token) => new Uri(WebBaseUrl, $"informes/baja?t={Uri.EscapeDataString(token)}").ToString();

    public string OneClickUnsubscribeUrl(string token) => new Uri(WebBaseUrl, $"informes/baja/un-clic?t={Uri.EscapeDataString(token)}").ToString();
}

/// <summary>The key of the unsubscribe tokens (derived from the JWT signing key at startup): never logged, never part of the settings.</summary>
public sealed class ReportUnsubscribeKey(byte[] key)
{
    public byte[] Key { get; } = key;

    public override string ToString() => "ReportUnsubscribeKey { }";
}
