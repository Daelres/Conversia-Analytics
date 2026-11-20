using Conversia.Application.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Tenants;

/// <summary>Wakes the deletion job of this instance (a deletion the administrator asked for); the job also runs every interval anyway.</summary>
public sealed class TenantDeletionSignal : ITenantDeletionSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled: the next run takes it.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>Runs <see cref="TenantDataDeleter"/> at startup, every <see cref="TenantDeletionOptions.IntervalMinutes"/> and when signalled.</summary>
public sealed class TenantDataDeletionService(
    TenantDataDeleter deleter, TenantDeletionSignal signal, TenantDeletionOptions options, ILogger<TenantDataDeletionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await deleter.RunAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError("The tenant data deletion run failed; it will be tried again ({ErrorType}).", ex.GetType().Name);
                }

                await signal.WaitAsync(TimeSpan.FromMinutes(options.IntervalMinutes), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }
}
