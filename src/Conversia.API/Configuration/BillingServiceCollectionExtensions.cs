using System.Globalization;
using Conversia.API.RateLimiting;
using Conversia.Application.Billing;
using Conversia.Application.Services;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Billing;
using Conversia.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.API.Configuration;

/// <summary>
/// The recurring charge of the subscription (docs/BILLING.md): the Wompi adapter, the dunning settings, the engine, the services, the job and
/// the rate limit of the events endpoint. <c>Billing:Wompi:Environment</c> switches the gateway on; without it the API boots with an
/// unconfigured gateway (charges are created, nothing is sent, the payment buttons answer 503). With it, every key is required and must carry
/// the prefix of that environment; a bad value stops the API at startup naming the key, never printing it.
/// </summary>
/// <summary>How old a signed event may be (<c>Billing:Events:MaxAgeHours</c>, 72: Wompi retries for 24 h); an older or future one is a replay.</summary>
public sealed record BillingEventsOptions(TimeSpan MaxAge)
{
    public static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(10);
}

public static class BillingServiceCollectionExtensions
{
    public const string EnvironmentKey = "Billing:Wompi:Environment";
    public const string PublicKeyKey = "Billing:Wompi:PublicKey";
    public const string PrivateKeyKey = "Billing:Wompi:PrivateKey";
    public const string EventsSecretKey = "Billing:Wompi:EventsSecret";
    public const string IntegritySecretKey = "Billing:Wompi:IntegritySecret";
    public const string TimeoutSecondsKey = "Billing:Wompi:TimeoutSeconds";

    public const string RetryDaysKey = "Billing:Dunning:RetryDays";
    public const string GraceDaysKey = "Billing:Dunning:GraceDays";
    public const string CancelAfterKey = "Billing:Dunning:CancelAfterSuspendedDays";
    public const string AttemptHourKey = "Billing:Dunning:AttemptHourLocal";
    public const string PaymentLinkDaysKey = "Billing:PaymentLinkValidDays";

    public const string JobEnabledKey = "Billing:Job:Enabled";
    public const string JobIntervalKey = "Billing:Job:IntervalSeconds";
    public const string JobBatchKey = "Billing:Job:BatchSize";
    public const string EventsPerMinuteKey = "Billing:Events:RateLimitPerMinute";
    public const string EventsFailedPerMinuteKey = "Billing:Events:FailedRateLimitPerMinute";
    public const string EventsMaxAgeHoursKey = "Billing:Events:MaxAgeHours";

    public static IServiceCollection AddBilling(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(GetValidatedSettings(configuration));

        var defaults = BillingJobOptions.Default;
        var job = new BillingJobOptions(
            ConfigurationReader.ReadBool(configuration, JobEnabledKey, defaults.Enabled),
            ConfigurationReader.ReadInt(configuration, JobIntervalKey, defaults.IntervalSeconds, 5, 3600),
            ConfigurationReader.ReadInt(configuration, JobBatchKey, defaults.BatchSize, 1, 10_000),
            defaults.EventMaxAttempts);
        services.AddSingleton(job);

        if (GetValidatedWompiOptions(configuration) is { } wompi)
        {
            // The secrets live in the options object from here on; the configuration must not keep a second copy.
            configuration[PrivateKeyKey] = null;
            configuration[EventsSecretKey] = null;
            configuration[IntegritySecretKey] = null;
            services.AddSingleton(wompi);

            // One attempt, a fixed timeout, no redirects (the key would follow them) and no handler logging (it can print headers).
            services.AddHttpClient<IPaymentGateway, WompiGateway>(client =>
                {
                    client.Timeout = wompi.Timeout;
                    client.MaxResponseContentBufferSize = WompiGateway.MaxResponseBytes;
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    ConnectTimeout = wompi.Timeout,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                })
                .RemoveAllLoggers();
        }
        else
        {
            services.AddSingleton<IPaymentGateway, UnconfiguredPaymentGateway>();
        }

        services.AddScoped<IBillingUnitOfWork, BillingUnitOfWork>();
        services.AddScoped<IBillingRepository, BillingRepository>();
        services.AddScoped<BillingEngine>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IAdminBillingService, AdminBillingService>();
        services.AddSingleton<BillingJobSignal>();
        services.AddSingleton<BillingJobRunner>();
        if (job.Enabled)
        {
            services.AddHostedService<BillingJobService>();
        }

        services.AddSingleton(new BillingEventsRateLimiter(
            ConfigurationReader.ReadInt(configuration, EventsFailedPerMinuteKey, 30, 1, 1_000_000),
            ConfigurationReader.ReadInt(configuration, EventsPerMinuteKey, 600, 1, 1_000_000)));
        services.AddSingleton(new BillingEventsOptions(TimeSpan.FromHours(ConfigurationReader.ReadInt(configuration, EventsMaxAgeHoursKey, 72, 1, 24 * 30))));
        return services;
    }

    public static BillingSettings GetValidatedSettings(IConfiguration configuration)
    {
        IReadOnlyList<int>? retryDays = null;
        var raw = configuration[RetryDaysKey];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var parsed = new List<int>();
            foreach (var part in parts)
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var day))
                {
                    throw new InvalidOperationException($"{RetryDaysKey} must be a comma-separated list of increasing whole numbers of days.");
                }

                parsed.Add(day);
            }

            retryDays = parsed;
        }

        try
        {
            return new BillingSettings(
                retryDays,
                ConfigurationReader.ReadInt(configuration, GraceDaysKey, BillingSettings.DefaultGraceDays, 1, BillingSettings.MaxDays),
                ConfigurationReader.ReadInt(configuration, CancelAfterKey, BillingSettings.DefaultCancelAfterSuspendedDays, 0, 365),
                ConfigurationReader.ReadInt(configuration, AttemptHourKey, BillingSettings.DefaultAttemptHourLocal, 0, 23),
                paymentLinkValidDays: ConfigurationReader.ReadInt(configuration, PaymentLinkDaysKey, 3, 1, 30));
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"{RetryDaysKey} must be at most 10 increasing whole numbers of days between 1 and {BillingSettings.MaxDays}.");
        }
    }

    /// <summary>Null when <c>Billing:Wompi:Environment</c> is not set (no gateway in this deployment).</summary>
    public static WompiOptions? GetValidatedWompiOptions(IConfiguration configuration)
    {
        var environmentRaw = configuration[EnvironmentKey];
        if (string.IsNullOrWhiteSpace(environmentRaw))
        {
            return null;
        }

        if (!Enum.TryParse<WompiEnvironment>(environmentRaw.Trim(), ignoreCase: true, out var environment) || !Enum.IsDefined(environment)
            || int.TryParse(environmentRaw, out _))
        {
            throw new InvalidOperationException($"{EnvironmentKey} must be 'Sandbox' or 'Production'.");
        }

        var prefixes = WompiOptions.PrefixesOf(environment);
        return new WompiOptions
        {
            Environment = environment,
            PublicKey = Key(configuration, PublicKeyKey, prefixes.Public, environment),
            PrivateKey = Key(configuration, PrivateKeyKey, prefixes.Private, environment),
            EventsSecret = Key(configuration, EventsSecretKey, prefixes.Events, environment),
            IntegritySecret = Key(configuration, IntegritySecretKey, prefixes.Integrity, environment),
            Timeout = TimeSpan.FromSeconds(ConfigurationReader.ReadInt(configuration, TimeoutSecondsKey, 15, 2, 60)),
        };
    }

    private static string Key(IConfiguration configuration, string key, string prefix, WompiEnvironment environment)
    {
        var value = configuration[key]?.Trim();
        if (string.IsNullOrEmpty(value) || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length <= prefix.Length
            || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new InvalidOperationException($"{key} is required with {EnvironmentKey} set and must be a key of the {environment} environment (prefix '{prefix}').");
        }

        return value;
    }
}
