using Conversia.Application.Classification;
using Conversia.Infrastructure.Classification;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.API.Configuration;

public static class ClassificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the classifier behind <see cref="IConversationClassifier"/> (<c>Classification:Provider</c>) and the background worker that
    /// classifies the conversations ingestion flags. With <c>Classification:Enabled=false</c> the worker is not started (conversations stay
    /// flagged). Invalid values stop the API at startup naming the key.
    /// </summary>
    public static IServiceCollection AddClassification(this IServiceCollection services, IConfiguration configuration)
    {
        var options = GetValidatedOptions(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);

        // The provider switch is the one place a new classifier plugs in (docs/CLASSIFICATION.md): an "Llm" provider would register its own
        // IConversationClassifier here, and nothing else changes.
        services.AddSingleton<IConversationClassifier>(_ => new RuleBasedConversationClassifier(LoadRules(options)));

        services.AddSingleton<ConversationClassificationRunner>();
        services.AddSingleton<ClassificationBacklogMonitor>();
        services.AddSingleton<ClassificationBacklogReporter>();

        if (options.Enabled)
        {
            services.AddHostedService<ConversationClassificationService>();
        }

        return services;
    }

    public static ClassificationOptions GetValidatedOptions(IConfiguration configuration)
    {
        var defaults = ClassificationOptions.Default;

        var provider = configuration[ClassificationOptions.ProviderKey];
        provider = string.IsNullOrWhiteSpace(provider) ? defaults.Provider : provider.Trim();
        if (!string.Equals(provider, ClassificationOptions.RulesProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{ClassificationOptions.ProviderKey} must be '{ClassificationOptions.RulesProvider}': it is the only classification provider implemented.");
        }

        var rulesPath = configuration[ClassificationOptions.RulesPathKey];
        var options = new ClassificationOptions(
            Enabled: ConfigurationReader.ReadBool(configuration, ClassificationOptions.EnabledKey, defaults.Enabled),
            Provider: ClassificationOptions.RulesProvider,
            DebounceSeconds: ConfigurationReader.ReadInt(configuration, ClassificationOptions.DebounceSecondsKey, defaults.DebounceSeconds, 0, 86_400),
            PollIntervalMilliseconds: ConfigurationReader.ReadInt(configuration, ClassificationOptions.PollIntervalMillisecondsKey, defaults.PollIntervalMilliseconds, 10, 600_000),
            BatchSize: ConfigurationReader.ReadInt(configuration, ClassificationOptions.BatchSizeKey, defaults.BatchSize, 1, 1000),
            MaxFailures: ConfigurationReader.ReadInt(configuration, ClassificationOptions.MaxFailuresKey, defaults.MaxFailures, 1, 100),
            RetryDelaySeconds: ConfigurationReader.ReadInt(configuration, ClassificationOptions.RetryDelaySecondsKey, defaults.RetryDelaySeconds, 0, 86_400),
            ProcessingTimeoutSeconds: ConfigurationReader.ReadInt(configuration, ClassificationOptions.ProcessingTimeoutSecondsKey, defaults.ProcessingTimeoutSeconds, 1, 3600),
            RulesPath: string.IsNullOrWhiteSpace(rulesPath) ? null : rulesPath.Trim());

        // A bad rules file stops the API now, not when the first conversation is read.
        _ = LoadRules(options);
        return options;
    }

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
