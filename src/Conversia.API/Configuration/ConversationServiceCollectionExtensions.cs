using Conversia.Application.Interfaces;
using Conversia.Application.Services;
using Conversia.Application.Time;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Conversations;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.API.Configuration;

public static class ConversationServiceCollectionExtensions
{
    public const string TimeZoneKey = "Metrics:TimeZone";

    /// <summary>
    /// Registers the conversation list and outcome API, the tenant time zone (<c>Metrics:TimeZone</c>) and the job that closes idle
    /// conversations. With <c>Conversations:AutoCloseEnabled=false</c> the job is not started. Invalid values stop the API at startup naming the key.
    /// </summary>
    public static IServiceCollection AddConversations(this IServiceCollection services, IConfiguration configuration)
    {
        var lifecycle = GetValidatedLifecycleOptions(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(GetValidatedTimeZone(configuration));
        services.AddSingleton(lifecycle);

        services.AddScoped<IConversationQueries, ConversationQueries>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<ConversationAutoCloser>();

        if (lifecycle.Enabled)
        {
            services.AddHostedService<ConversationLifecycleService>();
        }

        return services;
    }

    public static ConversationLifecycleOptions GetValidatedLifecycleOptions(IConfiguration configuration)
    {
        var defaults = ConversationLifecycleOptions.Default;
        return new ConversationLifecycleOptions(
            Enabled: ConfigurationReader.ReadBool(configuration, ConversationLifecycleOptions.EnabledKey, defaults.Enabled),
            AfterDays: ConfigurationReader.ReadInt(configuration, ConversationLifecycleOptions.AfterDaysKey, defaults.AfterDays, 1, 365),
            IntervalMinutes: ConfigurationReader.ReadInt(configuration, ConversationLifecycleOptions.IntervalMinutesKey, defaults.IntervalMinutes, 1, 1440),
            BatchSize: ConfigurationReader.ReadInt(configuration, ConversationLifecycleOptions.BatchSizeKey, defaults.BatchSize, 1, 5000));
    }

    public static TenantTimeZone GetValidatedTimeZone(IConfiguration configuration)
    {
        var id = configuration[TimeZoneKey];
        id = string.IsNullOrWhiteSpace(id) ? TenantTimeZone.DefaultId : id.Trim();
        try
        {
            return new TenantTimeZone(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException($"{TimeZoneKey} must be an IANA time zone name known to this machine (for example America/Bogota).");
        }
    }
}
