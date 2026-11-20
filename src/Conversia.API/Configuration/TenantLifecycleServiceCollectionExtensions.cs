using Conversia.Application.Interfaces;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Tenants;

namespace Conversia.API.Configuration;

public static class TenantLifecycleServiceCollectionExtensions
{
    public const string RetentionDaysKey = "Tenants:Deletion:RetentionDays";
    public const string EnabledKey = "Tenants:Deletion:Enabled";
    public const string IntervalMinutesKey = "Tenants:Deletion:IntervalMinutes";
    public const string BatchSizeKey = "Tenants:Deletion:BatchSize";

    /// <summary>
    /// The company lifecycle (docs/PLATFORM_ADMIN.md, "Lifecycle of a company"): the retention window of a cancelled company, the billing
    /// hooks unit 1c calls, and the deletion job. Reads and validates <c>Tenants:Deletion:*</c>; a bad value stops the API naming the key.
    /// </summary>
    public static IServiceCollection AddTenantLifecycle(this IServiceCollection services, IConfiguration configuration)
    {
        var defaults = TenantDeletionOptions.Default;
        services.AddSingleton(new TenantLifecycleSettings(ConfigurationReader.ReadInt(
            configuration, RetentionDaysKey, Tenant.DefaultDeletionRetentionDays, Tenant.MinDeletionRetentionDays, Tenant.MaxDeletionRetentionDays)));
        var options = new TenantDeletionOptions(
            ConfigurationReader.ReadBool(configuration, EnabledKey, defaults.Enabled),
            ConfigurationReader.ReadInt(configuration, IntervalMinutesKey, defaults.IntervalMinutes, 1, 1440),
            ConfigurationReader.ReadInt(configuration, BatchSizeKey, defaults.BatchSize, 100, 10_000));
        services.AddSingleton(options);

        services.AddSingleton<TenantDeletionSignal>();
        services.AddSingleton<ITenantDeletionSignal>(sp => sp.GetRequiredService<TenantDeletionSignal>());
        services.AddScoped<TenantLifecycleService>();
        services.AddScoped<ITenantBillingLifecycle>(sp => sp.GetRequiredService<TenantLifecycleService>());
        services.AddScoped<ITenantDeletionScheduler>(sp => sp.GetRequiredService<TenantLifecycleService>());
        services.AddSingleton<TenantDataDeleter>();

        // Switchable like every background job (the integration tests run the deleter directly).
        if (options.Enabled)
        {
            services.AddHostedService<TenantDataDeletionService>();
        }

        return services;
    }
}
