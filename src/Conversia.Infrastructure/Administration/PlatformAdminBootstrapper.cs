using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Conversia.Infrastructure.Administration;

/// <summary>
/// Creates the first platform administrator at startup from <c>PlatformAdmin:Email</c> and <c>PlatformAdmin:Password</c> when no
/// administrator with that email exists. It never changes an existing administrator (the configured password is ignored once the account
/// exists), touches the database only when both values are set, and logs no email or password.
/// </summary>
public sealed class PlatformAdminBootstrapper : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PlatformAdminOptions _options;
    private readonly ILogger<PlatformAdminBootstrapper> _logger;

    public PlatformAdminBootstrapper(IServiceScopeFactory scopeFactory, IOptions<PlatformAdminOptions> options, ILogger<PlatformAdminBootstrapper> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.BootstrapRequested)
        {
            return;
        }

        try
        {
            await BootstrapAsync(cancellationToken);
        }
        finally
        {
            // Done with it, whatever happened: the options object lives as long as the process and must not keep the password.
            _options.ClearPassword();
        }
    }

    private async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var admins = scope.ServiceProvider.GetRequiredService<IPlatformAdminRepository>();
        var email = _options.Email!.Trim();

        if (await admins.GetByEmailAsync(email, cancellationToken) is not null)
        {
            _logger.LogInformation("Platform administrator already exists; the configured password is not applied.");
            return;
        }

        var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>().HashPassword(_options.Password!);
        if (await admins.TryAddAsync(PlatformAdmin.Create(email, hash), cancellationToken))
        {
            _logger.LogInformation("Platform administrator created.");
        }
        else
        {
            _logger.LogInformation("Platform administrator already exists; the configured password is not applied.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
