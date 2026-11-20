using Conversia.Application.Classification.Llm;

namespace Conversia.Infrastructure.Configuration;

/// <summary>
/// The validated <c>Assistant:Llm</c> section, present only when <c>Assistant:Generator=Llm</c> (docs/ASSISTANT.md, "Configuration").
/// Holds no secret: the API key (<c>Anthropic:ApiKey</c>) is handed to the SDK client at startup only.
/// </summary>
/// <param name="Client"><c>Anthropic</c> (the real API) or <c>Replay</c> (canned answers, no key, Development only).</param>
/// <param name="DisableThinking">The reply call sends <c>thinking: disabled</c> (the default); the guard call always does.</param>
/// <param name="TimeoutSeconds">HTTP timeout of one call; the SDK never retries (<c>MaxRetries = 0</c>).</param>
/// <param name="AllowRealClientNonSandbox">With the real client, let real (non-sandbox) conversations reach it: only after the privacy review.</param>
/// <param name="TenantMonthlyUsdCap">What one company's assistant may spend a month (real conversations).</param>
/// <param name="SandboxMonthlyUsdPerCompany">What one company's sandbox conversations may spend a month on the assistant.</param>
/// <param name="KnowledgeTokenBudget">The company block's budget; a bigger catalog is cut to what the conversation is about.</param>
public sealed record AssistantLlmOptions(
    string Client,
    string Model,
    string Effort,
    bool DisableThinking,
    int MaxTokens,
    int GuardMaxTokens,
    int TimeoutSeconds,
    bool GuardEnabled,
    bool AllowRealClientNonSandbox,
    Uri BaseUrl,
    decimal TenantMonthlyUsdCap,
    decimal SandboxMonthlyUsdPerCompany,
    int KnowledgeTokenBudget,
    int BreakerFailures,
    int BreakerCooldownSeconds,
    LlmPricing Pricing)
{
    public const string GeneratorKey = "Assistant:Generator";
    public const string NoGenerator = "None";
    public const string LlmGenerator = "Llm";
    public const string SectionName = "Assistant:Llm";
    public const string ClientKey = SectionName + ":Client";
    public const string PrivacyApprovedKey = SectionName + ":PrivacyApproved";
    public const string ModelKey = SectionName + ":Model";
    public const string EffortKey = SectionName + ":Effort";
    public const string ThinkingKey = SectionName + ":Thinking";
    public const string MaxTokensKey = SectionName + ":MaxTokens";
    public const string GuardMaxTokensKey = SectionName + ":Guard:MaxTokens";
    public const string GuardEnabledKey = SectionName + ":Guard:Enabled";
    public const string GuardHandoffConfidenceKey = SectionName + ":Guard:HandoffConfidence";

    /// <summary>From this guard confidence a request for a person or a complaint hands off (0 to 1; 0.4 by default).</summary>
    public double GuardHandoffConfidence { get; init; } = 0.4;
    public const string TimeoutSecondsKey = SectionName + ":TimeoutSeconds";
    public const string AllowRealClientNonSandboxKey = SectionName + ":AllowRealClientNonSandbox";
    public const string BaseUrlKey = SectionName + ":BaseUrl";
    public const string TenantMonthlyUsdCapKey = SectionName + ":TenantMonthlyUsdCap";
    public const string SandboxMonthlyUsdPerCompanyKey = SectionName + ":SandboxMonthlyUsdPerCompany";
    public const string KnowledgeTokenBudgetKey = SectionName + ":KnowledgeTokenBudget";
    public const string BreakerFailuresKey = SectionName + ":Breaker:Failures";
    public const string BreakerCooldownSecondsKey = SectionName + ":Breaker:CooldownSeconds";
    public const string PricingPrefix = SectionName + ":Pricing:";

    public const string AnthropicClient = "Anthropic";
    public const string ReplayClient = "Replay";

    /// <summary>The model a sandbox conversation may reach: the real client only reaches real conversations once allowed.</summary>
    public bool ReachesRealConversations => Client == ReplayClient || AllowRealClientNonSandbox;

    public static AssistantLlmOptions Default { get; } = new(
        AnthropicClient, "claude-haiku-5-5", "low", DisableThinking: true, MaxTokens: 1024, GuardMaxTokens: 128, TimeoutSeconds: 12, GuardEnabled: true,
        AllowRealClientNonSandbox: false, new Uri("https://api.anthropic.com"), TenantMonthlyUsdCap: 5m, SandboxMonthlyUsdPerCompany: 1m, KnowledgeTokenBudget: 3000,
        BreakerFailures: 5, BreakerCooldownSeconds: 120, LlmPricing.Default);
}
