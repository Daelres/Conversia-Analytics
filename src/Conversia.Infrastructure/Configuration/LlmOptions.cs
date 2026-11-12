using Conversia.Application.Classification.Llm;

namespace Conversia.Infrastructure.Configuration;

/// <summary>
/// The validated <c>Classification:Llm</c> section, present only when <c>Classification:Provider=Llm</c> (docs/CLASSIFICATION.md, "The LLM
/// provider"). Holds no secret: the API key (<c>Anthropic:ApiKey</c>) is read once at startup and handed to the SDK client only.
/// </summary>
/// <param name="Client"><c>Anthropic</c> (the real API) or <c>Replay</c> (canned answers, Development only).</param>
/// <param name="Model">Model id sent as a plain string.</param>
/// <param name="Effort"><c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> or <c>max</c>.</param>
/// <param name="DisableThinking"><c>Classification:Llm:Thinking=Disabled</c>; the default (<c>Adaptive</c>) leaves thinking to the model.</param>
/// <param name="TimeoutSeconds">HTTP timeout of one call (Stage A: 1 to 8).</param>
/// <param name="MaxRetries">HTTP retries inside one call (Stage A: 0).</param>
/// <param name="OtherWaitsSeconds">Everything else the claim transaction may wait for (reads, writes), for the budget.</param>
/// <param name="DebounceSeconds">Quiet period before a conversation is read by the LLM (longer than the rules' one: a burst of messages costs one call).</param>
/// <param name="IncludeSandbox">Sandbox conversations go through the LLM too (default true in Development).</param>
/// <param name="ReplayPath">Canned answers of the replay client, keyed by snapshot hash (optional).</param>
public sealed record LlmOptions(
    string Client,
    string Model,
    string Effort,
    bool DisableThinking,
    int MaxTokens,
    int TimeoutSeconds,
    int MaxRetries,
    int OtherWaitsSeconds,
    int MaxFallbackRetries,
    bool PrivacyApproved,
    bool ExtractAgeRange,
    string? AgeRangeLegalRef,
    int BreakerFailures,
    int BreakerCooldownSeconds,
    int DebounceSeconds,
    bool IncludeSandbox,
    string? ReplayPath,
    LlmPricing Pricing,
    ConversationPreprocessorOptions Preprocessing,
    bool AllowRealClientNonSandbox = false,
    Uri? BaseUrl = null,
    byte[]? HashKey = null,
    int ClaimWarningMilliseconds = 5000)
{
    public const string AllowRealClientNonSandboxKey = SectionName + ":AllowRealClientNonSandbox";
    public const string BaseUrlKey = SectionName + ":BaseUrl";
    public const string HashKeyKey = SectionName + ":HashKey";
    public const string ClaimWarningMillisecondsKey = SectionName + ":ClaimWarningMilliseconds";

    /// <summary>Real (non-sandbox) conversations stay away from the real API unless allowed explicitly (after the privacy review).</summary>
    public bool SandboxOnly => Client == AnthropicClient && !AllowRealClientNonSandbox;

    /// <summary>Never prints the hash key.</summary>
    public override string ToString()
        => $"LlmOptions {{ Client = {Client}, Model = {Model}, Effort = {Effort}, TimeoutSeconds = {TimeoutSeconds}, DebounceSeconds = {DebounceSeconds}, IncludeSandbox = {IncludeSandbox}, AllowRealClientNonSandbox = {AllowRealClientNonSandbox}, BaseUrl = {BaseUrl}, Keyed = {HashKey is { Length: > 0 }} }}";

    public const string SectionName = "Classification:Llm";
    public const string ClientKey = SectionName + ":Client";
    public const string ModelKey = SectionName + ":Model";
    public const string EffortKey = SectionName + ":Effort";
    public const string ThinkingKey = SectionName + ":Thinking";
    public const string MaxTokensKey = SectionName + ":MaxTokens";
    public const string TimeoutSecondsKey = SectionName + ":TimeoutSeconds";
    public const string MaxRetriesKey = SectionName + ":MaxRetries";
    public const string OtherWaitsSecondsKey = SectionName + ":OtherWaitsSeconds";
    public const string TotalBudgetKey = SectionName + ":TotalBudget";
    public const string MaxFallbackRetriesKey = SectionName + ":MaxFallbackRetries";
    public const string PrivacyApprovedKey = SectionName + ":PrivacyApproved";
    public const string ExtractAgeRangeKey = SectionName + ":ExtractAgeRange";
    public const string AgeRangeLegalRefKey = SectionName + ":AgeRangeLegalRef";
    public const string BreakerFailuresKey = SectionName + ":Breaker:Failures";
    public const string BreakerCooldownSecondsKey = SectionName + ":Breaker:CooldownSeconds";
    public const string DebounceSecondsKey = SectionName + ":DebounceSeconds";
    public const string IncludeSandboxKey = SectionName + ":IncludeSandbox";
    public const string ReplayPathKey = SectionName + ":ReplayPath";
    public const string PricingInputKey = SectionName + ":Pricing:InputPerMTok";
    public const string PricingOutputKey = SectionName + ":Pricing:OutputPerMTok";
    public const string PricingCacheReadKey = SectionName + ":Pricing:CacheReadPerMTok";
    public const string PricingCacheWriteKey = SectionName + ":Pricing:CacheWritePerMTok";

    /// <summary>The API key of the Anthropic client. Never logged, never part of an options record.</summary>
    public const string ApiKeyKey = "Anthropic:ApiKey";

    public const string AnthropicClient = "Anthropic";
    public const string ReplayClient = "Replay";

    /// <summary>Stage A ceiling of <see cref="TimeoutSeconds"/> (design D4); the hardening slice may raise it.</summary>
    public const int MaxStageATimeoutSeconds = 8;

    /// <summary>Wait of a conversation while another one is the breaker's half-open probe.</summary>
    public static readonly TimeSpan ProbeDeferral = TimeSpan.FromSeconds(30);

    /// <summary>Most random seconds added to an open-breaker deferral.</summary>
    public const int MaxJitterSeconds = 30;

    /// <summary>
    /// The worst time the claim transaction can stay open on the LLM path: every HTTP attempt plus the other waits (design D4). Must be shorter
    /// than <c>Classification:ProcessingTimeoutSeconds</c> and <c>Classification:RetryDelaySeconds</c>.
    /// </summary>
    public int TotalBudgetSeconds => (TimeoutSeconds * (MaxRetries + 1)) + OtherWaitsSeconds;

    public TimeSpan Debounce => TimeSpan.FromSeconds(DebounceSeconds);

    public static LlmOptions Default(bool development) => new(
        Client: AnthropicClient, Model: "claude-haiku-5-5", Effort: "low", DisableThinking: false, MaxTokens: 2048, TimeoutSeconds: 8,
        MaxRetries: 0, OtherWaitsSeconds: 5, MaxFallbackRetries: 2, PrivacyApproved: false, ExtractAgeRange: false, AgeRangeLegalRef: null,
        BreakerFailures: 5, BreakerCooldownSeconds: 300, DebounceSeconds: 300, IncludeSandbox: development, ReplayPath: null,
        Pricing: LlmPricing.Default, Preprocessing: ConversationPreprocessorOptions.Default, AllowRealClientNonSandbox: false,
        BaseUrl: new Uri("https://api.anthropic.com"), HashKey: null, ClaimWarningMilliseconds: 5000);
}
