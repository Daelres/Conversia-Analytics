using Conversia.Application.Interfaces;
using Conversia.Application.Metrics;
using Conversia.Application.Services;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Metrics;

namespace Conversia.API.Configuration;

public static class MetricsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the metrics API. The time zone (<c>Metrics:TimeZone</c>) is registered by <c>AddConversations</c>; the other settings are
    /// validated here and a bad value stops the API at startup naming the key. See docs/METRICS.md.
    /// </summary>
    public static IServiceCollection AddMetrics(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(GetValidatedOptions(configuration));
        services.AddScoped<IMetricsQueries, MetricsQueries>();
        services.AddScoped<IMetricsService, MetricsService>();
        return services;
    }

    public static MetricsOptions GetValidatedOptions(IConfiguration configuration)
    {
        var defaults = MetricsOptions.Default;
        var options = new MetricsOptions(
            FirstResponseSlaMinutes: ConfigurationReader.ReadInt(configuration, MetricsOptions.FirstResponseSlaMinutesKey, defaults.FirstResponseSlaMinutes, 1, 1440),
            ConversionMaturityDays: ConfigurationReader.ReadInt(configuration, MetricsOptions.ConversionMaturityDaysKey, defaults.ConversionMaturityDays, 1, 365),
            TopLossReasons: ConfigurationReader.ReadInt(configuration, MetricsOptions.TopLossReasonsKey, defaults.TopLossReasons, 1, 20),
            MinSampleSize: ConfigurationReader.ReadInt(configuration, MetricsOptions.MinSampleSizeKey, defaults.MinSampleSize, 1, 100_000),
            ConversionDropAlertPoints: ConfigurationReader.ReadInt(configuration, MetricsOptions.ConversionDropAlertPointsKey, defaults.ConversionDropAlertPoints, 1, 100),
            BranchBelowAveragePoints: ConfigurationReader.ReadInt(configuration, MetricsOptions.BranchBelowAveragePointsKey, defaults.BranchBelowAveragePoints, 1, 100));

        // The conversion rate is computed over conversations old enough to have settled. The job that closes idle conversations (and gives the
        // silent ones up as lost) acts after AutoCloseAfterDays; a maturity shorter than that would rate conversations the job is still going to
        // decide, and the rate of a period would keep moving afterwards. Refusing to start is the safe choice: a KPI that drifts quietly is worse
        // than a deployment that says what to fix. With the job off there is nothing to wait for.
        var lifecycle = ConversationServiceCollectionExtensions.GetValidatedLifecycleOptions(configuration);
        if (lifecycle.Enabled && options.ConversionMaturityDays < lifecycle.AfterDays)
        {
            throw new InvalidOperationException(
                $"{MetricsOptions.ConversionMaturityDaysKey} must be at least {ConversationLifecycleOptions.AfterDaysKey}: a conversation is rated as mature only after the job that closes idle conversations has had its chance to decide it.");
        }

        return options;
    }
}
