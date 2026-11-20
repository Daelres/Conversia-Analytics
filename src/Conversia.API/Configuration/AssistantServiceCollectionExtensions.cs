using Conversia.Application.Assistant;
using Conversia.Application.Assistant.Llm;
using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Time;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Assistant;
using Conversia.Infrastructure.Classification.Llm;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.API.Configuration;

/// <summary>
/// The AI sales assistant (docs/ASSISTANT.md): settings, signals of the message paths, the reply port (the null generator in unit 5a, the
/// scripted one for sandbox conversations), the admission hook, the administrator's switches and counts, and the job. Invalid values stop the
/// API at startup naming the key.
/// </summary>
public static class AssistantServiceCollectionExtensions
{
    public const string TermsVersionKey = "Assistant:TermsVersion";
    public const string JobEnabledKey = "Assistant:JobEnabled";
    public const string PollSecondsKey = "Assistant:PollSeconds";
    public const string LeaseSecondsKey = "Assistant:LeaseSeconds";
    public const string BatchSizeKey = "Assistant:BatchSize";
    public const string GenerationTimeoutKey = "Assistant:GenerationTimeoutSeconds";
    public const string ContextMessagesKey = "Assistant:ContextMessages";

    public static IServiceCollection AddAssistant(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.TryAddSingleton(TimeProvider.System);
        var options = GetValidatedOptions(configuration);
        var llm = GetValidatedLlmOptions(configuration, environment.EnvironmentName, options);
        services.AddSingleton(options);
        services.AddScoped<IAssistantRepository, AssistantRepository>();
        services.AddScoped<IPlatformSwitchStore, PlatformSwitchStore>();
        services.AddScoped<IAssistantSignals, AssistantSignals>();
        services.AddScoped<IAssistantService, AssistantService>();
        services.AddScoped<IAssistantAdminService, AssistantAdminService>();
        services.AddScoped<IAssistantMonitoringQueries, AssistantMonitoringQueries>();
        services.TryAddSingleton<ScriptedAssistantReplyGenerator>();
        if (llm is null)
        {
            // Without the model (Assistant:Generator=None, the default): every real fired timer becomes a silent handoff ("Pendiente de
            // revisión"); sandbox conversations get the scripted answer, so the whole loop can be tried without any provider. Nothing costs.
            services.TryAddSingleton<IAssistantAdmission, UnmeteredAssistantAdmission>();
            services.TryAddSingleton<IAssistantReplyGenerator, NullAssistantReplyGenerator>();
            services.TryAddSingleton<IAssistantReplyGeneratorResolver>(sp => new AssistantReplyGeneratorResolver(
                sp.GetRequiredService<IAssistantReplyGenerator>(), sp.GetRequiredService<ScriptedAssistantReplyGenerator>()));
        }
        else
        {
            AddModel(services, llm, configuration[LlmOptions.ApiKeyKey]);
        }

        services.AddSingleton<AssistantRunner>();
        if (options.JobEnabled)
        {
            services.AddHostedService<AssistantJobService>();
        }

        return services;
    }

    /// <summary>
    /// Unit 5b (docs/ASSISTANT.md, "The model"): the two clients (reply and guard: separate token ceilings, no SDK retry, pinned endpoint), the
    /// generator, its own circuit breaker (assistant outages never stop the nightly analysis and the other way round) and the metered admission.
    /// Sandbox conversations always get the model; real ones only with the Replay client or <c>Assistant:Llm:AllowRealClientNonSandbox</c>
    /// (the null generator otherwise: a silent handoff, never a paid call before the privacy review).
    /// </summary>
    private static void AddModel(IServiceCollection services, AssistantLlmOptions llm, string? apiKey)
    {
        services.AddSingleton(llm);
        services.TryAddSingleton(sp => Clients(llm, apiKey));
        services.AddSingleton<AssistantUsageMeter>();
        services.TryAddSingleton<IAssistantAdmission>(sp => sp.GetRequiredService<AssistantUsageMeter>());
        services.AddSingleton(sp => new LlmAssistantReplyGenerator(
            sp.GetRequiredService<AssistantLlmClients>(),
            new AssistantPromptBuilder(new ConversationPreprocessor(ConversationPreprocessorOptions.Default, ClassificationRuleSet.LoadDefault())),
            new LlmCircuitBreaker(llm.BreakerFailures, TimeSpan.FromSeconds(llm.BreakerCooldownSeconds), sp.GetRequiredService<ILoggerFactory>().CreateLogger("AssistantLlmCircuitBreaker")),
            new AssistantLlmSettings(llm.GuardEnabled, llm.KnowledgeTokenBudget, llm.Pricing, sp.GetRequiredService<TenantTimeZone>().Zone, llm.GuardHandoffConfidence),
            sp.GetRequiredService<ILogger<LlmAssistantReplyGenerator>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IAssistantReplyGenerator>(sp => sp.GetRequiredService<LlmAssistantReplyGenerator>());
        services.TryAddSingleton<IAssistantReplyGeneratorResolver>(sp => new LlmAssistantReplyGeneratorResolver(
            sp.GetRequiredService<IAssistantReplyGenerator>(), llm.ReachesRealConversations));
    }

    /// <summary>The reply and guard clients: Replay, or the official SDK with the key, the validated base URL, the timeout and NO retry.</summary>
    public static AssistantLlmClients Clients(AssistantLlmOptions llm, string? apiKey)
    {
        if (llm.Client == AssistantLlmOptions.ReplayClient)
        {
            var replay = new ReplayAssistantLlmClient();
            return new AssistantLlmClients(replay, replay);
        }

        var sdk = AnthropicLlmClient.CreateSdkClient(apiKey!, llm.BaseUrl, TimeSpan.FromSeconds(llm.TimeoutSeconds), maxRetries: 0);
        return new AssistantLlmClients(
            new AnthropicLlmClient(sdk, new AnthropicRequestSettings(llm.Model, llm.MaxTokens, llm.Effort, llm.DisableThinking)),
            new AnthropicLlmClient(sdk, new AnthropicRequestSettings(llm.Model, llm.GuardMaxTokens, "low", DisableThinking: true)));
    }

    /// <summary>
    /// <c>Assistant:Generator</c> and the <c>Assistant:Llm</c> section; null without the model. Like the analysis (docs/CLASSIFICATION.md): the
    /// model is accepted only in Development until its release gate is lifted, requires the privacy approval and, with the real client, the key.
    /// </summary>
    public static AssistantLlmOptions? GetValidatedLlmOptions(IConfiguration configuration, string environmentName, AssistantOptions assistant)
    {
        var generator = ClassificationServiceCollectionExtensions.ReadChoice(
            configuration, AssistantLlmOptions.GeneratorKey, AssistantLlmOptions.NoGenerator, AssistantLlmOptions.NoGenerator, AssistantLlmOptions.LlmGenerator);
        if (generator == AssistantLlmOptions.NoGenerator)
        {
            return null;
        }

        if (!string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{AssistantLlmOptions.GeneratorKey}='{AssistantLlmOptions.LlmGenerator}' is accepted only in the Development environment until the release gate is lifted (docs/ASSISTANT.md).");
        }

        var defaults = AssistantLlmOptions.Default;
        var client = ClassificationServiceCollectionExtensions.ReadChoice(
            configuration, AssistantLlmOptions.ClientKey, defaults.Client, AssistantLlmOptions.AnthropicClient, AssistantLlmOptions.ReplayClient);
        if (!ConfigurationReader.ReadBool(configuration, AssistantLlmOptions.PrivacyApprovedKey, false))
        {
            throw new InvalidOperationException(
                $"{AssistantLlmOptions.PrivacyApprovedKey} must be 'true' to use the model: it records that the privacy review (docs/decisiones.md) approved sending pseudonymized conversations to the provider.");
        }

        if (client == AssistantLlmOptions.AnthropicClient && string.IsNullOrWhiteSpace(configuration[LlmOptions.ApiKeyKey]))
        {
            throw new InvalidOperationException(
                $"{LlmOptions.ApiKeyKey} is required when {AssistantLlmOptions.ClientKey}='{AssistantLlmOptions.AnthropicClient}' (set it from the environment, Anthropic__ApiKey, or Key Vault; never in a file of the repository).");
        }

        var model = configuration[AssistantLlmOptions.ModelKey];
        model = string.IsNullOrWhiteSpace(model) ? defaults.Model : model.Trim();
        if (model.Length > 100 || !ClassificationServiceCollectionExtensions.ModelPattern().IsMatch(model))
        {
            throw new InvalidOperationException($"{AssistantLlmOptions.ModelKey} must be a model id of lower-case letters, digits, dots and hyphens.");
        }

        var effort = ClassificationServiceCollectionExtensions.ReadChoice(configuration, AssistantLlmOptions.EffortKey, defaults.Effort, "low", "medium", "high", "xhigh", "max");
        var thinking = ClassificationServiceCollectionExtensions.ReadChoice(configuration, AssistantLlmOptions.ThinkingKey, "Disabled", "Adaptive", "Disabled");
        if (thinking == "Disabled" && effort is "xhigh" or "max")
        {
            throw new InvalidOperationException($"{AssistantLlmOptions.ThinkingKey}='Disabled' is accepted only with {AssistantLlmOptions.EffortKey} 'low', 'medium' or 'high'.");
        }

        var timeout = ConfigurationReader.ReadInt(configuration, AssistantLlmOptions.TimeoutSecondsKey, defaults.TimeoutSeconds, 1, 30);
        // The guard, the reply and one regeneration must fit in the job's generation budget (a timeout there is a handoff, never a late send).
        if ((timeout * 3) + 5 > assistant.GenerationTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"{AssistantLlmOptions.TimeoutSecondsKey} x 3 + 5 ({(timeout * 3) + 5} s) must not exceed {GenerationTimeoutKey} ({assistant.GenerationTimeoutSeconds} s): the guard, the reply and one regeneration run inside it.");
        }

        var handoffConfidence = 0.4;
        var rawConfidence = configuration[AssistantLlmOptions.GuardHandoffConfidenceKey];
        if (!string.IsNullOrWhiteSpace(rawConfidence)
            && (!double.TryParse(rawConfidence, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out handoffConfidence) || handoffConfidence is < 0 or > 1))
        {
            throw new InvalidOperationException($"{AssistantLlmOptions.GuardHandoffConfidenceKey} must be a number between 0 and 1 (a dot as decimal mark).");
        }

        return new AssistantLlmOptions(
            client,
            model,
            effort,
            thinking == "Disabled",
            ConfigurationReader.ReadInt(configuration, AssistantLlmOptions.MaxTokensKey, defaults.MaxTokens, 256, 8000),
            ConfigurationReader.ReadInt(configuration, AssistantLlmOptions.GuardMaxTokensKey, defaults.GuardMaxTokens, 64, 1024),
            timeout,
            ConfigurationReader.ReadBool(configuration, AssistantLlmOptions.GuardEnabledKey, defaults.GuardEnabled),
            ConfigurationReader.ReadBool(configuration, AssistantLlmOptions.AllowRealClientNonSandboxKey, defaults.AllowRealClientNonSandbox),
            ClassificationServiceCollectionExtensions.ReadBaseUrl(configuration, AssistantLlmOptions.BaseUrlKey, defaults.BaseUrl),
            ReadUsd(configuration, AssistantLlmOptions.TenantMonthlyUsdCapKey, defaults.TenantMonthlyUsdCap),
            ReadUsd(configuration, AssistantLlmOptions.SandboxMonthlyUsdPerCompanyKey, defaults.SandboxMonthlyUsdPerCompany),
            ConfigurationReader.ReadInt(configuration, AssistantLlmOptions.KnowledgeTokenBudgetKey, defaults.KnowledgeTokenBudget, 500, 20_000),
            ConfigurationReader.ReadInt(configuration, AssistantLlmOptions.BreakerFailuresKey, defaults.BreakerFailures, 1, 100),
            ConfigurationReader.ReadInt(configuration, AssistantLlmOptions.BreakerCooldownSecondsKey, defaults.BreakerCooldownSeconds, 1, 3600),
            new LlmPricing(
                ClassificationServiceCollectionExtensions.ReadPrice(configuration, AssistantLlmOptions.PricingPrefix + "InputPerMTok", defaults.Pricing.InputPerMTok),
                ClassificationServiceCollectionExtensions.ReadPrice(configuration, AssistantLlmOptions.PricingPrefix + "OutputPerMTok", defaults.Pricing.OutputPerMTok),
                ClassificationServiceCollectionExtensions.ReadPrice(configuration, AssistantLlmOptions.PricingPrefix + "CacheReadPerMTok", defaults.Pricing.CacheReadPerMTok),
                ClassificationServiceCollectionExtensions.ReadPrice(configuration, AssistantLlmOptions.PricingPrefix + "CacheWritePerMTok", defaults.Pricing.CacheWritePerMTok)))
        {
            GuardHandoffConfidence = handoffConfidence,
        };
    }

    private static decimal ReadUsd(IConfiguration configuration, string key, decimal defaultValue)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) && value is >= 0m and <= 100_000m
            ? value
            : throw new InvalidOperationException($"{key} must be a number of USD between 0 and 100000 (a dot as decimal mark).");
    }

    public static AssistantOptions GetValidatedOptions(IConfiguration configuration)
    {
        var defaults = new AssistantOptions();
        var terms = configuration[TermsVersionKey];
        if (terms is not null && (string.IsNullOrWhiteSpace(terms) || terms.Trim().Length > Domain.Entities.AssistantSettings.TermsVersionMaxLength))
        {
            throw new InvalidOperationException($"{TermsVersionKey} must be 1 to {Domain.Entities.AssistantSettings.TermsVersionMaxLength} characters.");
        }

        return new AssistantOptions
        {
            TermsVersion = terms?.Trim() ?? defaults.TermsVersion,
            JobEnabled = ConfigurationReader.ReadBool(configuration, JobEnabledKey, defaults.JobEnabled),
            PollSeconds = ConfigurationReader.ReadInt(configuration, PollSecondsKey, defaults.PollSeconds, 1, 60),
            LeaseSeconds = ConfigurationReader.ReadInt(configuration, LeaseSecondsKey, defaults.LeaseSeconds, 30, 3600),
            BatchSize = ConfigurationReader.ReadInt(configuration, BatchSizeKey, defaults.BatchSize, 1, 500),
            GenerationTimeoutSeconds = ConfigurationReader.ReadInt(configuration, GenerationTimeoutKey, defaults.GenerationTimeoutSeconds, 5, 300),
            ContextMessages = ConfigurationReader.ReadInt(configuration, ContextMessagesKey, defaults.ContextMessages, 5, 100),
            LateCapMinutes = ConfigurationReader.ReadInt(configuration, "Assistant:LateCapMinutes", defaults.LateCapMinutes, 1, 1440),
            MaxAttempts = ConfigurationReader.ReadInt(configuration, "Assistant:MaxAttempts", defaults.MaxAttempts, 1, 20),
            MaxParallel = ConfigurationReader.ReadInt(configuration, "Assistant:MaxParallel", defaults.MaxParallel, 1, 64),
            SendTimeoutSeconds = ConfigurationReader.ReadInt(configuration, "Assistant:SendTimeoutSeconds", defaults.SendTimeoutSeconds, 5, 120),
            TypingTimeoutSeconds = ConfigurationReader.ReadInt(configuration, "Assistant:TypingTimeoutSeconds", defaults.TypingTimeoutSeconds, 1, 30),
            HumanRequestDelaySeconds = ConfigurationReader.ReadInt(configuration, "Assistant:HumanRequestDelaySeconds", defaults.HumanRequestDelaySeconds, 1, 60),
            TenantMessagesPerHour = ConfigurationReader.ReadInt(configuration, "Assistant:TenantMessagesPerHour", defaults.TenantMessagesPerHour, 1, 1_000_000),
            ConversationMessagesPerHour = ConfigurationReader.ReadInt(configuration, "Assistant:ConversationMessagesPerHour", defaults.ConversationMessagesPerHour, 1, 100),
            ClarificationLines = ReadClarificationLines(configuration) ?? defaults.ClarificationLines,
        };
    }

    public const string ClarificationLinesKey = "Assistant:ClarificationLines";

    // Optional: the company-neutral lines said when the assistant did not understand (1 to 10 lines of 1 to 300 characters, no links).
    private static IReadOnlyList<string>? ReadClarificationLines(IConfiguration configuration)
    {
        var lines = configuration.GetSection(ClarificationLinesKey).GetChildren().Select(c => c.Value?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();
        if (lines.Count == 0)
        {
            return null;
        }

        var bad = lines.FindIndex(l => l.Length > 300 || Domain.Common.LinkRules.HasLink(l));
        if (lines.Count > 10 || bad >= 0)
        {
            throw new InvalidOperationException($"{ClarificationLinesKey}{(bad >= 0 ? ":" + bad : string.Empty)}: the clarification lines must be 1 to 10 lines of at most 300 characters, without links.");
        }

        return lines;
    }
}
