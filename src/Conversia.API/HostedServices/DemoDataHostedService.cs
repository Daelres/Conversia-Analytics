using Conversia.Application.Interfaces;

namespace Conversia.API.HostedServices;

/// <summary>
/// Seeds the demo data while the host starts. <see cref="StartAsync"/> awaits the seeding (unlike a
/// <c>BackgroundService</c>), so the data exists before the first request is served. A failure is logged and never
/// stops the API; a later explicit seeding (for instance after the migrations ran) still works.
/// </summary>
internal sealed class DemoDataHostedService(IServiceScopeFactory scopeFactory, ILogger<DemoDataHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDemoDataSeeder>().SeedAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Seeding the demo data failed; the API keeps running without it. Is the database reachable and migrated?");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
