using System.Text;

namespace Conversia.Infrastructure.Configuration;

/// <summary>The validated <c>Webhooks:Processing</c> section: the background worker that turns stored webhook events into conversations.</summary>
public sealed record WebhookProcessingOptions(
    bool Enabled,
    int PollIntervalMilliseconds,
    int BatchSize,
    int MaxAttempts,
    int RetryBaseDelaySeconds,
    int ProcessingTimeoutSeconds,
    int MaxMessagesPerDelivery,
    int TenantBusyDelaySeconds,
    int TenantLockWaitMilliseconds)
{
    public const string SectionName = "Webhooks:Processing";
    public const string EnabledKey = SectionName + ":Enabled";
    public const string PollIntervalMillisecondsKey = SectionName + ":PollIntervalMilliseconds";
    public const string BatchSizeKey = SectionName + ":BatchSize";
    public const string MaxAttemptsKey = SectionName + ":MaxAttempts";
    public const string RetryBaseDelaySecondsKey = SectionName + ":RetryBaseDelaySeconds";
    public const string ProcessingTimeoutSecondsKey = SectionName + ":ProcessingTimeoutSeconds";
    public const string MaxMessagesPerDeliveryKey = SectionName + ":MaxMessagesPerDelivery";
    public const string TenantBusyDelaySecondsKey = SectionName + ":TenantBusyDelaySeconds";
    public const string TenantLockWaitMillisecondsKey = SectionName + ":TenantLockWaitMilliseconds";

    public static WebhookProcessingOptions Default { get; } = new(
        Enabled: true, PollIntervalMilliseconds: 2000, BatchSize: 20, MaxAttempts: 8, RetryBaseDelaySeconds: 30,
        ProcessingTimeoutSeconds: 60, MaxMessagesPerDelivery: 500, TenantBusyDelaySeconds: 10, TenantLockWaitMilliseconds: 2000);

    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMilliseconds);

    public TimeSpan RetryBaseDelay => TimeSpan.FromSeconds(RetryBaseDelaySeconds);

    /// <summary>How long an event stays leased to the worker that claimed it: twice the processing timeout plus a margin for the bookkeeping.</summary>
    public TimeSpan Lease => TimeSpan.FromSeconds((2L * ProcessingTimeoutSeconds) + 10);

    public TimeSpan TenantLockWait => TimeSpan.FromMilliseconds(TenantLockWaitMilliseconds);
}

/// <summary>
/// The <c>Dialog360</c> section. <see cref="ApiKey"/> is bound from <c>Dialog360:ApiKey</c> (environment <c>Dialog360__ApiKey</c>) but
/// nothing reads it yet: this phase makes no outbound call to 360dialog. A class with its own <c>ToString</c>, never a record,
/// so the key cannot be printed by accident.
/// </summary>
public sealed class Dialog360Options
{
    public const string SectionName = "Dialog360";

    public string? ApiKey { get; set; }

    public override string ToString() => $"Dialog360Options {{ ApiKeyConfigured = {!string.IsNullOrEmpty(ApiKey)} }}";
}

/// <summary>The validated <c>Webhooks:Retention</c> section: how long processed and failed webhook events (which hold customers' phone numbers and message text) are kept.</summary>
public sealed record WebhookRetentionOptions(bool Enabled, int RetentionDays, int FailedRetentionDays)
{
    public const string SectionName = "Webhooks:Retention";
    public const string EnabledKey = SectionName + ":Enabled";
    public const string RetentionDaysKey = SectionName + ":RetentionDays";
    public const string FailedRetentionDaysKey = SectionName + ":FailedRetentionDays";

    public static WebhookRetentionOptions Default { get; } = new(Enabled: true, RetentionDays: 14, FailedRetentionDays: 60);
}

/// <summary>The validated <c>Webhooks:Health</c> section: when the queue is reported as degraded by <c>GET /health</c>.</summary>
public sealed record WebhookHealthOptions(int MaxPendingAgeMinutes, int MaxFailedPerHour)
{
    public const string SectionName = "Webhooks:Health";
    public const string MaxPendingAgeMinutesKey = SectionName + ":MaxPendingAgeMinutes";
    public const string MaxFailedPerHourKey = SectionName + ":MaxFailedPerHour";

    public static WebhookHealthOptions Default { get; } = new(MaxPendingAgeMinutes: 10, MaxFailedPerHour: 5);
}
