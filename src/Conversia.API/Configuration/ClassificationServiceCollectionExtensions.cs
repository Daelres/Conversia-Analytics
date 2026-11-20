using System.Globalization;
using System.Text.RegularExpressions;
using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Classification.Scheduling;
using Conversia.Infrastructure.Classification;
using Conversia.Infrastructure.Classification.Llm;
using Conversia.Infrastructure.Classification.Usage;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.API.Configuration;

public static partial class ClassificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the classifier behind <see cref="IConversationClassifier"/> (<c>Classification:Provider</c>) and the background worker that
    /// classifies the conversations ingestion flags. With <c>Classification:Enabled=false</c> the worker is not started (conversations stay
    /// flagged). With <c>Provider=Llm</c> it also registers the LLM pipeline (client, classifier, breaker, coordinator). Invalid values stop
    /// the API at startup naming the key, never printing a value.
    /// </summary>
    public static IServiceCollection AddClassification(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = GetValidatedOptions(configuration, environment.EnvironmentName);
        var rules = LoadRules(options);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);

        // The AI quota settings are read whatever the provider: the administrator sees each company's effective quota (docs/PLATFORM_ADMIN.md).
        services.AddSingleton(PlatformAdministrationServiceCollectionExtensions.GetValidatedQuota(configuration));
        services.AddSingleton<AiUsageMeter>(); // also the purge of the distinct conversation sets (UsageRetentionService)
        services.AddSingleton<IAiUsageLossCounter>(sp => sp.GetRequiredService<AiUsageMeter>());

        // The rules classifier is always there: it is the Rules provider, the LLM's fallback and the Won guard's reference.
        services.AddSingleton(_ => new RuleBasedConversationClassifier(rules));
        services.AddSingleton<IConversationClassifier>(sp => sp.GetRequiredService<RuleBasedConversationClassifier>());

        if (options.Llm is { } llm)
        {
            ValidatePoolSize(configuration.GetConnectionString("DefaultConnection"), llm);
            AddLlm(services, options, llm, rules, configuration[LlmOptions.ApiKeyKey]);
        }

        services.AddSingleton<ConversationClassificationRunner>();
        services.AddSingleton<ClassificationBacklogMonitor>();
        services.AddSingleton<ClassificationBacklogReporter>();
        services.AddSingleton(options.ScheduleSettings);

        if (options.Enabled)
        {
            // The rules read every conversation near real time; with Provider=Llm the LLM reads them on each company's schedule.
            services.AddHostedService<ConversationClassificationService>();
            if (options.Llm is not null)
            {
                services.AddHostedService<LlmAnalysisService>();
            }
        }

        return services;
    }

    private static void AddLlm(IServiceCollection services, ClassificationOptions options, LlmOptions llm, ClassificationRuleSet rules, string? apiKey)
    {
        var catalog = LlmCatalog.FromRules(rules);
        services.AddSingleton(llm);
        services.AddSingleton<ILlmClient>(_ => llm.Client == LlmOptions.ReplayClient
            ? ReplayLlmClient.Load(llm.ReplayPath, llm.ExtractAgeRange)
            : new AnthropicLlmClient(
                AnthropicLlmClient.CreateSdkClient(apiKey!, llm.BaseUrl ?? AnthropicLlmClient.OfficialBaseUrl, TimeSpan.FromSeconds(llm.TimeoutSeconds), llm.MaxRetries),
                new AnthropicRequestSettings(llm.Model, llm.MaxTokens, llm.Effort, llm.DisableThinking)));
        services.AddSingleton(sp => new LlmConversationClassifier(
            new ConversationPreprocessor(llm.Preprocessing, rules),
            new LlmPromptBuilder(new ExtractionSchema(catalog, llm.ExtractAgeRange)),
            new LlmResultMapper(catalog, llm.ExtractAgeRange),
            sp.GetRequiredService<ILlmClient>(),
            new LlmClassifierSettings(llm.Client == LlmOptions.ReplayClient ? ReplayLlmClient.ModelId : llm.Model, llm.ExtractAgeRange, llm.Preprocessing.Fingerprint,
                rules.Fingerprint(), llm.HashKey)));
        // Message Batches (Nightly companies, Classification:Llm:Batch:Enabled): their own SDK client (a bigger upload, 60 s) with NO retry:
        // creating a batch is not idempotent, and a lost answer is recovered from the provider's list (LlmAnalysisRunner.RecoverAbandonedBatchesAsync).
        services.AddSingleton<ILlmBatchClient>(sp => llm.Client == LlmOptions.ReplayClient
            ? new ReplayLlmBatchClient(sp.GetRequiredService<ILlmClient>())
            : AnthropicLlmBatchClient.Create(
                apiKey!, llm.BaseUrl ?? AnthropicLlmClient.OfficialBaseUrl, new AnthropicRequestSettings(llm.Model, llm.MaxTokens, llm.Effort, llm.DisableThinking)));
        services.AddSingleton(sp => new LlmAnalysisRunner(
            sp.GetRequiredService<IServiceScopeFactory>(), options, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<LlmAnalysisRunner>>(),
            sp.GetRequiredService<ClassificationCoordinator>(), sp.GetRequiredService<ILlmBatchClient>()));
        services.AddSingleton<LlmAnalysisScheduler>();
        services.AddSingleton(sp => new LlmCircuitBreaker(
            llm.BreakerFailures, TimeSpan.FromSeconds(llm.BreakerCooldownSeconds), sp.GetRequiredService<ILoggerFactory>().CreateLogger<LlmCircuitBreaker>()));
        // The metered admission and usage record (design D12, D13, D18): every company starts with the AI off until the administrator switches it on.
        services.TryAddSingleton<IAiUsageMeter>(sp => sp.GetRequiredService<AiUsageMeter>());
        services.AddSingleton(sp => new ClassificationCoordinator(
            sp.GetRequiredService<RuleBasedConversationClassifier>(),
            sp.GetRequiredService<LlmConversationClassifier>(),
            sp.GetRequiredService<LlmCircuitBreaker>(),
            sp.GetRequiredService<IAiUsageMeter>(),
            new LlmCoordinatorSettings(
                llm.MaxFallbackRetries, options.RetryDelay, llm.IncludeSandbox, LlmOptions.ProbeDeferral, LlmOptions.MaxJitterSeconds, llm.Pricing, llm.SandboxOnly),
            new Random(),
            sp.GetRequiredService<TimeProvider>()));
    }

    /// <summary>The options as a non-Development host would read them (the LLM provider is rejected there).</summary>
    public static ClassificationOptions GetValidatedOptions(IConfiguration configuration) => GetValidatedOptions(configuration, Environments.Production);

    public static ClassificationOptions GetValidatedOptions(IConfiguration configuration, string environmentName)
    {
        var defaults = ClassificationOptions.Default;

        var provider = configuration[ClassificationOptions.ProviderKey];
        provider = string.IsNullOrWhiteSpace(provider) ? defaults.Provider : provider.Trim();
        if (string.Equals(provider, ClassificationOptions.ShadowProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ClassificationOptions.ProviderKey}: '{ClassificationOptions.ShadowProvider}' is not available yet (it arrives with the classification runs); use '{ClassificationOptions.RulesProvider}' or '{ClassificationOptions.LlmProvider}'.");
        }

        var isLlm = string.Equals(provider, ClassificationOptions.LlmProvider, StringComparison.OrdinalIgnoreCase);
        if (!isLlm && !string.Equals(provider, ClassificationOptions.RulesProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ClassificationOptions.ProviderKey} must be '{ClassificationOptions.RulesProvider}' or '{ClassificationOptions.LlmProvider}'.");
        }

        var rulesPath = configuration[ClassificationOptions.RulesPathKey];
        var options = new ClassificationOptions(
            Enabled: ConfigurationReader.ReadBool(configuration, ClassificationOptions.EnabledKey, defaults.Enabled),
            Provider: isLlm ? ClassificationOptions.LlmProvider : ClassificationOptions.RulesProvider,
            DebounceSeconds: ConfigurationReader.ReadInt(configuration, ClassificationOptions.DebounceSecondsKey, defaults.DebounceSeconds, 0, 86_400),
            PollIntervalMilliseconds: ConfigurationReader.ReadInt(configuration, ClassificationOptions.PollIntervalMillisecondsKey, defaults.PollIntervalMilliseconds, 10, 600_000),
            BatchSize: ConfigurationReader.ReadInt(configuration, ClassificationOptions.BatchSizeKey, defaults.BatchSize, 1, 1000),
            MaxFailures: ConfigurationReader.ReadInt(configuration, ClassificationOptions.MaxFailuresKey, defaults.MaxFailures, 1, 100),
            RetryDelaySeconds: ConfigurationReader.ReadInt(configuration, ClassificationOptions.RetryDelaySecondsKey, defaults.RetryDelaySeconds, 0, 86_400),
            ProcessingTimeoutSeconds: ConfigurationReader.ReadInt(configuration, ClassificationOptions.ProcessingTimeoutSecondsKey, defaults.ProcessingTimeoutSeconds, 1, 3600),
            RulesPath: string.IsNullOrWhiteSpace(rulesPath) ? null : rulesPath.Trim());

        // A bad rules file stops the API now, not when the first conversation is read.
        _ = LoadRules(options);

        // The schedule is read whatever the provider: the administrator sees each company's windows.
        options = options with { Schedule = GetValidatedSchedule(configuration) };

        return isLlm ? options with { Llm = GetValidatedLlmOptions(configuration, options, environmentName) } : options;
    }

    /// <summary>The <c>Classification:Schedule</c> section (docs/CLASSIFICATION.md, "When the AI reads"); invalid values name their key.</summary>
    public static AnalysisScheduleSettings GetValidatedSchedule(IConfiguration configuration)
    {
        var defaults = AnalysisScheduleSettings.Default;
        var zoneId = configuration[AnalysisScheduleSettings.TimeZoneKey];
        var zone = defaults.TimeZone;
        if (!string.IsNullOrWhiteSpace(zoneId) && !string.Equals(zoneId.Trim(), AiQuotaSettings.BogotaTimeZoneId, StringComparison.Ordinal))
        {
            zone = TimeZoneInfo.TryFindSystemTimeZoneById(zoneId.Trim(), out var found)
                ? found
                : throw new InvalidOperationException($"{AnalysisScheduleSettings.TimeZoneKey} must be an IANA time zone id (America/Bogota).");
        }

        return new AnalysisScheduleSettings(
            zone,
            ConfigurationReader.ReadInt(configuration, AnalysisScheduleSettings.DefaultNightlyHourKey, defaults.DefaultNightlyHour, 0, 23),
            ConfigurationReader.ReadInt(configuration, AnalysisScheduleSettings.NightlyWindowHoursKey, defaults.NightlyWindowHours, 1, 12),
            ConfigurationReader.ReadInt(configuration, AnalysisScheduleSettings.SpreadMinutesKey, defaults.SpreadMinutes, 0, 180),
            ConfigurationReader.ReadInt(configuration, AnalysisScheduleSettings.MaxConversationsPerWindowKey, defaults.MaxConversationsPerWindow, 1, 1_000_000));
    }

    /// <summary>
    /// Design D20: the database pool must hold every LLM worker's connections. <c>Maximum Pool Size</c> of the connection string (Npgsql's
    /// default 100) must be at least <c>MaxConcurrent x 2 + 1 (webhook worker) + Workers + 20</c>; otherwise the API refuses to start, naming
    /// both values. Not checked without a connection string (tests that replace the database).
    /// </summary>
    public static void ValidatePoolSize(string? connectionString, LlmOptions llm)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var pool = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize;
        var needed = (llm.MaxConcurrent * 2) + 1 + llm.Workers + 20;
        if (pool < needed)
        {
            throw new InvalidOperationException(
                $"The connection string's Maximum Pool Size ({pool}) is too small for {LlmOptions.MaxConcurrentKey} ({llm.MaxConcurrent}) and {LlmOptions.WorkersKey} ({llm.Workers}): it needs at least {needed} (MaxConcurrent x 2 + 1 + Workers + 20).");
        }
    }

    /// <summary>
    /// Review: a batch request's custom id carries a prefix of the snapshot hash, which leaves the process. Outside Development (once the gate
    /// is lifted) batch mode therefore requires the keyed hash (<c>Classification:Llm:HashKey</c>), so the prefix cannot be confirmed by
    /// hashing a guessed conversation.
    /// </summary>
    public static void ValidateBatchHashKey(string environmentName, LlmOptions llm)
    {
        ArgumentNullException.ThrowIfNull(llm);
        if (llm.BatchEnabled && llm.HashKey is not { Length: > 0 } && !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{LlmOptions.BatchEnabledKey}=true requires {LlmOptions.HashKeyKey} outside Development: the batch custom id carries a prefix of the conversation hash.");
        }
    }

    /// <summary>
    /// The <c>Classification:Llm</c> section, read only for <c>Provider=Llm</c> (design D17, D22). Order matters for the messages: the client
    /// and the environment gate first, then the opt-ins, then ranges, then the Stage A timing budget.
    /// </summary>
    private static LlmOptions GetValidatedLlmOptions(IConfiguration configuration, ClassificationOptions options, string environmentName)
    {
        var development = string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);
        var defaults = LlmOptions.Default(development);

        var client = ReadChoice(configuration, LlmOptions.ClientKey, defaults.Client, LlmOptions.AnthropicClient, LlmOptions.ReplayClient);
        if (client == LlmOptions.ReplayClient && !development)
        {
            throw new InvalidOperationException($"{LlmOptions.ClientKey}='{LlmOptions.ReplayClient}' is accepted only in the Development environment.");
        }

        if (!development)
        {
            throw new InvalidOperationException(
                $"{ClassificationOptions.ProviderKey}='{ClassificationOptions.LlmProvider}' is accepted only in the Development environment until the release gate is lifted (docs/CLASSIFICATION.md).");
        }

        if (!ConfigurationReader.ReadBool(configuration, LlmOptions.PrivacyApprovedKey, defaults.PrivacyApproved))
        {
            throw new InvalidOperationException(
                $"{LlmOptions.PrivacyApprovedKey} must be 'true' to use the LLM provider: it records that the privacy review (docs/decisiones.md) approved sending pseudonymized conversations to the provider.");
        }

        if (client == LlmOptions.AnthropicClient && string.IsNullOrWhiteSpace(configuration[LlmOptions.ApiKeyKey]))
        {
            throw new InvalidOperationException(
                $"{LlmOptions.ApiKeyKey} is required when {LlmOptions.ClientKey}='{LlmOptions.AnthropicClient}' (set it from the environment, Anthropic__ApiKey, or Key Vault; never in a file of the repository).");
        }

        var model = configuration[LlmOptions.ModelKey];
        model = string.IsNullOrWhiteSpace(model) ? defaults.Model : model.Trim();
        if (model.Length > 100 || !ModelPattern().IsMatch(model))
        {
            throw new InvalidOperationException($"{LlmOptions.ModelKey} must be a model id of lower-case letters, digits, dots and hyphens.");
        }

        var effort = ReadChoice(configuration, LlmOptions.EffortKey, defaults.Effort, "low", "medium", "high", "xhigh", "max");
        var thinking = ReadChoice(configuration, LlmOptions.ThinkingKey, "Adaptive", "Adaptive", "Disabled");
        if (thinking == "Disabled" && effort is "xhigh" or "max")
        {
            throw new InvalidOperationException($"{LlmOptions.ThinkingKey}='Disabled' is accepted only with {LlmOptions.EffortKey} 'low', 'medium' or 'high'.");
        }

        var extractAgeRange = ConfigurationReader.ReadBool(configuration, LlmOptions.ExtractAgeRangeKey, defaults.ExtractAgeRange);
        var legalRef = configuration[LlmOptions.AgeRangeLegalRefKey];
        legalRef = string.IsNullOrWhiteSpace(legalRef) ? null : legalRef.Trim();
        if (extractAgeRange && legalRef is null)
        {
            throw new InvalidOperationException(
                $"{LlmOptions.ExtractAgeRangeKey}=true requires {LlmOptions.AgeRangeLegalRefKey}: the reference of the legal sign-off for asking the age range (it can include minors, Ley 1581 art. 7).");
        }

        var replayPath = configuration[LlmOptions.ReplayPathKey];
        replayPath = string.IsNullOrWhiteSpace(replayPath) ? null : replayPath.Trim();
        if (client == LlmOptions.ReplayClient)
        {
            _ = ReplayLlmClient.Load(replayPath, extractAgeRange); // a bad file stops the API now
        }

        var preprocessing = new ConversationPreprocessorOptions(
            ConfigurationReader.ReadInt(configuration, ConversationPreprocessorOptions.AdvisorMaxCharactersKey, defaults.Preprocessing.AdvisorMaxCharacters, ConversationPreprocessorOptions.MinAdvisorMaxCharacters, ConversationPreprocessorOptions.MaxAdvisorMaxCharacters),
            ConfigurationReader.ReadInt(configuration, ConversationPreprocessorOptions.TokenBudgetKey, defaults.Preprocessing.TokenBudget, ConversationPreprocessorOptions.MinTokenBudget, ConversationPreprocessorOptions.MaxTokenBudget),
            ConfigurationReader.ReadInt(configuration, ConversationPreprocessorOptions.KeepLastLinesKey, defaults.Preprocessing.KeepLastLines, ConversationPreprocessorOptions.MinKeepLastLines, ConversationPreprocessorOptions.MaxKeepLastLines));
        preprocessing.Validate();

        var llm = new LlmOptions(
            Client: client,
            Model: model,
            Effort: effort,
            DisableThinking: thinking == "Disabled",
            MaxTokens: ConfigurationReader.ReadInt(configuration, LlmOptions.MaxTokensKey, defaults.MaxTokens, 256, 16_000),
            TimeoutSeconds: ConfigurationReader.ReadInt(configuration, LlmOptions.TimeoutSecondsKey, defaults.TimeoutSeconds, 1, LlmOptions.MaxTimeoutSeconds),
            MaxRetries: ConfigurationReader.ReadInt(configuration, LlmOptions.MaxRetriesKey, defaults.MaxRetries, 0, LlmOptions.MaxHttpRetries),
            OtherWaitsSeconds: ConfigurationReader.ReadInt(configuration, LlmOptions.OtherWaitsSecondsKey, defaults.OtherWaitsSeconds, 0, 60),
            MaxFallbackRetries: ConfigurationReader.ReadInt(configuration, LlmOptions.MaxFallbackRetriesKey, defaults.MaxFallbackRetries, 0, 10),
            PrivacyApproved: true,
            ExtractAgeRange: extractAgeRange,
            AgeRangeLegalRef: legalRef,
            BreakerFailures: ConfigurationReader.ReadInt(configuration, LlmOptions.BreakerFailuresKey, defaults.BreakerFailures, 1, 100),
            BreakerCooldownSeconds: ConfigurationReader.ReadInt(configuration, LlmOptions.BreakerCooldownSecondsKey, defaults.BreakerCooldownSeconds, 1, 3600),
            DebounceSeconds: ConfigurationReader.ReadInt(configuration, LlmOptions.DebounceSecondsKey, defaults.DebounceSeconds, 0, 86_400),
            IncludeSandbox: ConfigurationReader.ReadBool(configuration, LlmOptions.IncludeSandboxKey, defaults.IncludeSandbox),
            ReplayPath: replayPath,
            Pricing: new LlmPricing(
                ReadPrice(configuration, LlmOptions.PricingInputKey, defaults.Pricing.InputPerMTok),
                ReadPrice(configuration, LlmOptions.PricingOutputKey, defaults.Pricing.OutputPerMTok),
                ReadPrice(configuration, LlmOptions.PricingCacheReadKey, defaults.Pricing.CacheReadPerMTok),
                ReadPrice(configuration, LlmOptions.PricingCacheWriteKey, defaults.Pricing.CacheWritePerMTok)),
            Preprocessing: preprocessing,
            AllowRealClientNonSandbox: ConfigurationReader.ReadBool(configuration, LlmOptions.AllowRealClientNonSandboxKey, defaults.AllowRealClientNonSandbox),
            BaseUrl: ReadBaseUrl(configuration, defaults.BaseUrl!),
            HashKey: ReadHashKey(configuration),
            ClaimWarningMilliseconds: ConfigurationReader.ReadInt(configuration, LlmOptions.ClaimWarningMillisecondsKey, defaults.ClaimWarningMilliseconds, 100, 60_000));

        var maxConcurrent = ConfigurationReader.ReadInt(configuration, LlmOptions.MaxConcurrentKey, defaults.MaxConcurrent, 1, 16);
        llm = llm with
        {
            MaxConcurrent = maxConcurrent,
            // Design D20: as many workers as calls in flight by default (more would only wait for a permit).
            Workers = ConfigurationReader.ReadInt(configuration, LlmOptions.WorkersKey, maxConcurrent, 1, 16),
            BatchEnabled = ConfigurationReader.ReadBool(configuration, LlmOptions.BatchEnabledKey, defaults.BatchEnabled),
            BatchMaxRequests = ConfigurationReader.ReadInt(configuration, LlmOptions.BatchMaxRequestsKey, defaults.BatchMaxRequests, 1, 100_000),
            BatchPollSeconds = ConfigurationReader.ReadInt(configuration, LlmOptions.BatchPollSecondsKey, defaults.BatchPollSeconds, 1, 3600),
            BatchDiscountFactor = ReadFactor(configuration, LlmOptions.BatchDiscountFactorKey, defaults.BatchDiscountFactor),
            PerCompanyConcurrency = ConfigurationReader.ReadInt(configuration, LlmOptions.PerCompanyConcurrencyKey, defaults.PerCompanyConcurrency, 1, 16),
            BatchPoisonGraceHours = ConfigurationReader.ReadInt(configuration, LlmOptions.BatchPoisonGraceHoursKey, defaults.BatchPoisonGraceHours, 1, 24 * 60),
            BatchRetentionDays = ConfigurationReader.ReadInt(configuration, LlmOptions.BatchRetentionDaysKey, defaults.BatchRetentionDays, 1, 3650),
        };
        ValidateBatchHashKey(environmentName, llm);

        // Design H1: the call runs outside any lock, between a lease (phase 1) and a short relock (phase 3). Its worst case must end before
        // the lease (the retry delay) expires, so a conversation is never claimed twice while its call is in flight. No relation with the
        // transaction timeout (no transaction is open during the call) or with any debounce.
        var budget = llm.TotalBudgetSeconds;
        if (budget >= options.RetryDelaySeconds)
        {
            throw new InvalidOperationException(
                $"{LlmOptions.TotalBudgetKey} ({LlmOptions.TimeoutSecondsKey} x ({LlmOptions.MaxRetriesKey} + 1) + {LlmOptions.OtherWaitsSecondsKey} = {budget} s) must be shorter than {ClassificationOptions.RetryDelaySecondsKey} ({options.RetryDelaySeconds} s), the lease of an analysis in flight.");
        }

        return llm;
    }

    internal static string ReadChoice(IConfiguration configuration, string key, string defaultValue, params string[] accepted)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return accepted.FirstOrDefault(a => string.Equals(a, raw.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{key} must be one of: {string.Join(", ", accepted)}.");
    }

    // The endpoint the prompts and the key go to: only from validated configuration (never ANTHROPIC_BASE_URL), https, no credentials in it.
    private static Uri ReadBaseUrl(IConfiguration configuration, Uri defaultValue) => ReadBaseUrl(configuration, LlmOptions.BaseUrlKey, defaultValue);

    internal static Uri ReadBaseUrl(IConfiguration configuration, string key, Uri defaultValue)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            ? uri
            : throw new InvalidOperationException($"{key} must be an absolute https URL without credentials, query or fragment.");
    }

    // Optional secret of the snapshot hash (HMAC-SHA256), like Messaging:CredentialEncryptionKey: base64 of at least 32 random bytes.
    private static byte[]? ReadHashKey(IConfiguration configuration)
    {
        var raw = configuration[LlmOptions.HashKeyKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(raw.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{LlmOptions.HashKeyKey} must be base64 of at least 32 random bytes (openssl rand -base64 32).");
        }

        return key.Length >= 32
            ? key
            : throw new InvalidOperationException($"{LlmOptions.HashKeyKey} must be base64 of at least 32 random bytes (openssl rand -base64 32).");
    }

    private static decimal ReadFactor(IConfiguration configuration, string key, decimal defaultValue)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value is >= 0m and <= 1m
            ? value
            : throw new InvalidOperationException($"{key} must be a number between 0 and 1 (the share of the list price a batched request costs).");
    }

    internal static decimal ReadPrice(IConfiguration configuration, string key, decimal defaultValue)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value is >= 0m and <= 1000m
            ? value
            : throw new InvalidOperationException($"{key} must be a number of USD per million tokens between 0 and 1000.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*$")]
    internal static partial Regex ModelPattern();

    private static ClassificationRuleSet LoadRules(ClassificationOptions options)
    {
        if (options.RulesPath is null)
        {
            return ClassificationRuleSet.LoadDefault();
        }

        try
        {
            return ClassificationRuleSet.LoadFromFile(options.RulesPath);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ClassificationOptions.RulesPathKey}: {ex.Message}");
        }
    }
}
