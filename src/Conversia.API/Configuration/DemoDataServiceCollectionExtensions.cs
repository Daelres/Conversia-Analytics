using Conversia.API.HostedServices;
using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Seeding;

namespace Conversia.API.Configuration;

public static class DemoDataServiceCollectionExtensions
{
    /// <summary>Registers the demo data seeding only when <c>Demo:Enabled</c> is true (validated, see <see cref="DemoDataOptionsValidator"/>).</summary>
    public static IServiceCollection AddDemoData(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = DemoDataOptionsValidator.GetValidatedOptions(configuration, environment);
        if (options is null)
        {
            return services;
        }

        services.AddSingleton(options);
        services.AddScoped<IDemoDataSeeder, DemoDataSeeder>();
        services.AddHostedService<DemoDataHostedService>();
        return services;
    }
}
