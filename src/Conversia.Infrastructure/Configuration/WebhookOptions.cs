using System.Text;

namespace Conversia.Infrastructure.Configuration;

/// <summary>The validated <c>Webhooks:Processing</c> section: the background worker that turns stored webhook events into conversations.</summary>
public sealed record WebhookProcessingOptions(
    bool Enabled,
    int PollIntervalMilliseconds,
    int BatchSize,
    int MaxAttempts,
    int RetryBaseDelaySeconds)
{
    public const string SectionName = "Webhooks:Processing";
    public const string EnabledKey = SectionName + ":Enabled";
    public const string PollIntervalMillisecondsKey = SectionName + ":PollIntervalMilliseconds";
    public const string BatchSizeKey = SectionName + ":BatchSize";
    public const string MaxAttemptsKey = SectionName + ":MaxAttempts";
    public const string RetryBaseDelaySecondsKey = SectionName + ":RetryBaseDelaySeconds";

    public static WebhookProcessingOptions Default { get; } = new(
        Enabled: true, PollIntervalMilliseconds: 2000, BatchSize: 20, MaxAttempts: 5, RetryBaseDelaySeconds: 30);

    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMilliseconds);

    public TimeSpan RetryBaseDelay => TimeSpan.FromSeconds(RetryBaseDelaySeconds);
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
