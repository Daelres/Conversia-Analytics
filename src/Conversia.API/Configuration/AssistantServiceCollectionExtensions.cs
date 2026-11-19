using Conversia.Application.Assistant;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Assistant;
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

    public static IServiceCollection AddAssistant(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        var options = GetValidatedOptions(configuration);
        services.AddSingleton(options);
        services.AddScoped<IAssistantRepository, AssistantRepository>();
        services.AddScoped<IPlatformSwitchStore, PlatformSwitchStore>();
        services.AddScoped<IAssistantSignals, AssistantSignals>();
        services.AddScoped<IAssistantService, AssistantService>();
        services.AddScoped<IAssistantAdminService, AssistantAdminService>();
        services.AddScoped<IAssistantMonitoringQueries, AssistantMonitoringQueries>();
        services.TryAddSingleton<IAssistantAdmission, UnmeteredAssistantAdmission>();

        // Unit 5a: no model answers real customers. Every real fired timer becomes a silent handoff ("Pendiente de revisión"); sandbox
        // conversations get the scripted answer, so the whole loop can be tried without any provider.
        services.TryAddSingleton<IAssistantReplyGenerator, NullAssistantReplyGenerator>();
        services.TryAddSingleton<ScriptedAssistantReplyGenerator>();
        services.TryAddSingleton<IAssistantReplyGeneratorResolver>(sp => new AssistantReplyGeneratorResolver(
            sp.GetRequiredService<IAssistantReplyGenerator>(), sp.GetRequiredService<ScriptedAssistantReplyGenerator>()));

        services.AddSingleton<AssistantRunner>();
        if (options.JobEnabled)
        {
            services.AddHostedService<AssistantJobService>();
        }

        return services;
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
        };
    }
}
