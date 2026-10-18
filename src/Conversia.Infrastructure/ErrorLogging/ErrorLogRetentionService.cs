using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.ErrorLogging;

/// <summary>
/// Deletes the error rows older than <c>ErrorLogging:RetentionDays</c>: once at startup and then every 24 hours. It is a
/// <see cref="BackgroundService"/>, so the startup purge runs beside the host and never delays readiness; a failure is logged
/// and the next run tries again.
/// </summary>
public sealed class ErrorLogRetentionService(
    IServiceScopeFactory scopeFactory,
    ErrorLoggingOptions options,
    TimeProvider timeProvider,
    ILogger<ErrorLogRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var timer = new PeriodicTimer(Interval, timeProvider);
        try
        {
            do
            {
                await PurgeOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    private async Task PurgeOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-options.RetentionDays);
            using var scope = scopeFactory.CreateScope();
            var deleted = await scope.ServiceProvider.GetRequiredService<IErrorLogPurger>().PurgeAsync(cutoff, cancellationToken);
            if (deleted > 0)
            {
                logger.LogInformation("Error log retention purged {Deleted} rows older than {RetentionDays} days.", deleted, options.RetentionDays);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "The error log retention purge failed; it will be tried again at the next run.");
        }
    }
}
