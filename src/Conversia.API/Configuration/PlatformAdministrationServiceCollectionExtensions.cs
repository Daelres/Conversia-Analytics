using System.Globalization;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Interfaces;
using Conversia.Application.Licensing;
using Conversia.Application.Monitoring;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Classification.Usage;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Messaging;
using Conversia.Infrastructure.Monitoring;
using Conversia.Infrastructure.Repositories;

namespace Conversia.API.Configuration;

public static class PlatformAdministrationServiceCollectionExtensions
{
    public const string QuotaDefaultMonthlyConversationsKey = "Classification:Quota:DefaultMonthlyConversations";
    public const string QuotaUsdPerQuotaUnitKey = "Classification:Quota:TenantMonthlyUsdPerQuotaUnit";
    public const string QuotaDeploymentMonthlyUsdCapKey = "Classification:Quota:DeploymentMonthlyUsdCap";
    public const string QuotaTimeZoneKey = "Classification:Quota:TimeZone";
    public const string QuotaSandboxMonthlyUsdPerCompanyKey = "Classification:Quota:SandboxMonthlyUsdPerCompany";
    public const string UsageRetentionEnabledKey = "Usage:Retention:Enabled";
    public const string SendLimitRefreshEnabledKey = "Messaging:TenantSendLimitRefresh:Enabled";

    /// <summary>
    /// The platform administration beyond companies and lines (docs/PLATFORM_ADMIN.md): per-company limits, the line licence inventory and the
    /// monitoring. Reads and validates <c>Licensing:*</c> and <c>PlatformMonitoring:*</c>; a bad value stops the API naming the key. Call after
    /// <c>AddClassification</c> and <c>AddMessaging</c> (it uses their options).
    /// </summary>
    public static IServiceCollection AddPlatformAdministration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new LicensingSettings(ConfigurationReader.ReadBool(configuration, LicensingSettings.RequireAssignedLicenseKey, false)));
        services.AddSingleton(GetValidatedThresholds(configuration));
        services.AddSingleton(sp =>
        {
            var sending = sp.GetRequiredService<MessageSendingOptions>();
            return new PlatformLimitDefaults(sending.TenantSendRateLimitPerMinute, sp.GetRequiredService<AiQuotaSettings>(), sending.TenantSendRateLimitMaxPerMinute);
        });

        services.AddSingleton<TenantSendRateLimits>();
        services.AddSingleton<ITenantSendRateLimits>(sp => sp.GetRequiredService<TenantSendRateLimits>());
        // Both background jobs can be switched off (the integration tests do, like the other workers): without the refresher an instance
        // only knows the per-company send limits saved through itself.
        if (ConfigurationReader.ReadBool(configuration, SendLimitRefreshEnabledKey, true))
        {
            services.AddHostedService<TenantSendRateLimitRefresher>();
        }

        services.AddSingleton<MessageSendOutcomeRecorder>();
        services.AddSingleton<IMessageSendOutcomeRecorder>(sp => sp.GetRequiredService<MessageSendOutcomeRecorder>());
        if (ConfigurationReader.ReadBool(configuration, UsageRetentionEnabledKey, true))
        {
            services.AddHostedService<UsageRetentionService>();
        }

        services.AddScoped<ILineLicenseRepository, LineLicenseRepository>();
        services.AddScoped<ILineLicenseService, LineLicenseService>();
        services.AddScoped<IPlatformMonitoringQueries, PlatformMonitoringQueries>();
        services.AddScoped<IPlatformHealthProbe, HealthCheckServiceProbe>();
        services.AddSingleton<IClassificationEngineStatus, ClassificationEngineStatus>();
        services.AddSingleton<PlatformOverviewCache>();
        services.AddScoped<IPlatformMonitoringService, PlatformMonitoringService>();
        return services;
    }

    /// <summary><c>Classification:Quota:*</c> (design D12, D18): read whatever the provider, since the administrator sees the effective quota.</summary>
    public static AiQuotaSettings GetValidatedQuota(IConfiguration configuration)
    {
        var defaults = AiQuotaSettings.Default;
        var zoneId = configuration[QuotaTimeZoneKey];
        var zone = defaults.TimeZone;
        if (!string.IsNullOrWhiteSpace(zoneId) && !string.Equals(zoneId.Trim(), AiQuotaSettings.BogotaTimeZoneId, StringComparison.Ordinal))
        {
            zone = TimeZoneInfo.TryFindSystemTimeZoneById(zoneId.Trim(), out var found)
                ? found
                : throw new InvalidOperationException($"{QuotaTimeZoneKey} must be an IANA time zone id known to this system (for example '{AiQuotaSettings.BogotaTimeZoneId}').");
        }

        return new AiQuotaSettings(
            ConfigurationReader.ReadInt(configuration, QuotaDefaultMonthlyConversationsKey, defaults.DefaultMonthlyConversations, 0, Domain.Entities.Tenant.MaxAiMonthlyConversationQuota),
            ReadMoney(configuration, QuotaUsdPerQuotaUnitKey, defaults.UsdPerQuotaUnit, 100m),
            ReadMoney(configuration, QuotaDeploymentMonthlyUsdCapKey, defaults.DeploymentMonthlyUsdCap, 1_000_000m),
            zone,
            ReadMoney(configuration, QuotaSandboxMonthlyUsdPerCompanyKey, defaults.SandboxMonthlyUsdPerCompany, 10_000m));
    }

    public static MonitoringThresholds GetValidatedThresholds(IConfiguration configuration)
    {
        var d = MonitoringThresholds.Default;
        int Read(string name, int value, int min, int max) => ConfigurationReader.ReadInt(configuration, $"{MonitoringThresholds.SectionName}:{name}", value, min, max);
        return new MonitoringThresholds(
            WebhookBacklog: Read(nameof(d.WebhookBacklog), d.WebhookBacklog, 1, 10_000_000),
            WebhookOldestPendingMinutes: Read(nameof(d.WebhookOldestPendingMinutes), d.WebhookOldestPendingMinutes, 1, 10_080),
            ClassificationBacklog: Read(nameof(d.ClassificationBacklog), d.ClassificationBacklog, 1, 10_000_000),
            ClassificationOldestMinutes: Read(nameof(d.ClassificationOldestMinutes), d.ClassificationOldestMinutes, 1, 10_080),
            FailedDeliveryRatePercent: Read(nameof(d.FailedDeliveryRatePercent), d.FailedDeliveryRatePercent, 1, 100),
            MinDeliveriesForRate: Read(nameof(d.MinDeliveriesForRate), d.MinDeliveriesForRate, 1, 1_000_000),
            SilentLineHours: Read(nameof(d.SilentLineHours), d.SilentLineHours, 1, 720),
            ErrorsLast24h: Read(nameof(d.ErrorsLast24h), d.ErrorsLast24h, 1, 1_000_000),
            SendFailureRatePercent: Read(nameof(d.SendFailureRatePercent), d.SendFailureRatePercent, 1, 100),
            MinSendsForRate: Read(nameof(d.MinSendsForRate), d.MinSendsForRate, 1, 1_000_000),
            OverviewCacheSeconds: Read(nameof(d.OverviewCacheSeconds), d.OverviewCacheSeconds, 0, 300));
    }

    private static decimal ReadMoney(IConfiguration configuration, string key, decimal defaultValue, decimal max)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0m && value <= max
            ? value
            : throw new InvalidOperationException($"{key} must be a number of USD between 0 and {max.ToString(CultureInfo.InvariantCulture)}.");
    }
}
