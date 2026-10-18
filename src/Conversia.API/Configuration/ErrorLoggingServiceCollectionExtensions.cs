using Conversia.API.ErrorLogging;
using Conversia.API.RateLimiting;
using Conversia.Application.ErrorLogging;
using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.ErrorLogging;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.API.Configuration;

public static class ErrorLoggingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the error registry, the retention purge and the report endpoint's key and rate limit from the validated
    /// <c>ErrorLogging</c> section. With <c>Enabled=false</c> nothing is stored and the retention service is not started.
    /// </summary>
    public static IServiceCollection AddErrorLogging(this IServiceCollection services, IConfiguration configuration)
    {
        var options = ErrorLoggingOptionsValidator.GetValidatedOptions(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(new ErrorReportKeyVerifier(options.ReportKey));
        services.AddSingleton(sp => new ErrorStormGuard(options.MaxPerFingerprintPerMinute, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IErrorRegistry, ErrorRegistry>();
        services.AddScoped<IErrorLogPurger, ErrorLogPurger>();
        services.AddErrorReportRateLimiting(options);

        if (options.Enabled)
        {
            services.AddHostedService<ErrorLogRetentionService>();
        }

        return services;
    }
}
