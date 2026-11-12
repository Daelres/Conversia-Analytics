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
    string? RulesPath,
    LlmOptions? Llm = null)
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

    /// <summary>The rule-based Spanish classifier: the default, and the rollback of every LLM stage.</summary>
    public const string RulesProvider = "Rules";

    /// <summary>The LLM provider (<see cref="Llm"/> holds its settings). Development only until the release gate is lifted.</summary>
    public const string LlmProvider = "Llm";

    /// <summary>Rules and LLM side by side (design PR 3): not available yet; rejected at startup.</summary>
    public const string ShadowProvider = "Shadow";

    /// <summary>The quiet period the claim waits for: the LLM's own, longer one when the provider is the LLM.</summary>
    public TimeSpan EffectiveDebounce => Llm is null ? Debounce : Llm.Debounce;

    public static ClassificationOptions Default { get; } = new(
        Enabled: true, Provider: RulesProvider, DebounceSeconds: 60, PollIntervalMilliseconds: 2000, BatchSize: 20,
        MaxFailures: 5, RetryDelaySeconds: 60, ProcessingTimeoutSeconds: 30, RulesPath: null);

    public TimeSpan Debounce => TimeSpan.FromSeconds(DebounceSeconds);

    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMilliseconds);

    public TimeSpan RetryDelay => TimeSpan.FromSeconds(RetryDelaySeconds);
}
