namespace Conversia.Infrastructure.Configuration;

/// <summary>
/// The validated <c>Classification</c> section: which classifier reads the conversations, and the background worker that feeds it. A
/// conversation is read some time after its last message (<see cref="DebounceSeconds"/>), so a chat in full swing is read once it settles,
/// not once per message.
/// </summary>
public sealed record ClassificationOptions(
    bool Enabled,
    string Provider,
    int DebounceSeconds,
    int PollIntervalMilliseconds,
    int BatchSize,
    int MaxFailures,
    int RetryDelaySeconds,
    int ProcessingTimeoutSeconds,
    string? RulesPath)
{
    public const string SectionName = "Classification";
    public const string EnabledKey = SectionName + ":Enabled";
    public const string ProviderKey = SectionName + ":Provider";
    public const string DebounceSecondsKey = SectionName + ":DebounceSeconds";
    public const string PollIntervalMillisecondsKey = SectionName + ":PollIntervalMilliseconds";
    public const string BatchSizeKey = SectionName + ":BatchSize";
    public const string MaxFailuresKey = SectionName + ":MaxFailures";
    public const string RetryDelaySecondsKey = SectionName + ":RetryDelaySeconds";
    public const string ProcessingTimeoutSecondsKey = SectionName + ":ProcessingTimeoutSeconds";
    public const string RulesPathKey = SectionName + ":RulesPath";

    /// <summary>The only provider so far: the rule-based Spanish classifier. A future LLM adapter would add its own name here.</summary>
    public const string RulesProvider = "Rules";

    public static ClassificationOptions Default { get; } = new(
        Enabled: true, Provider: RulesProvider, DebounceSeconds: 60, PollIntervalMilliseconds: 2000, BatchSize: 20,
        MaxFailures: 5, RetryDelaySeconds: 60, ProcessingTimeoutSeconds: 30, RulesPath: null);

    public TimeSpan Debounce => TimeSpan.FromSeconds(DebounceSeconds);

    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMilliseconds);

    public TimeSpan RetryDelay => TimeSpan.FromSeconds(RetryDelaySeconds);
}
