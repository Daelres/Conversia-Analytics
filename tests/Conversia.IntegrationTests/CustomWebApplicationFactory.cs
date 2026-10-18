using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Conversia.IntegrationTests;

/// <summary>
/// Boots the real API in-process (via <see cref="WebApplicationFactory{TEntryPoint}"/>)
/// against a real PostgreSQL instance started in a Testcontainers container —
/// no EF Core InMemory, no local PostgreSQL, per docs/ARCHITECTURE.md.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine")
            // The suites open many hosts and pools at once; the default of 100 connections runs out. fsync off: throw-away data.
            .WithCommand("-c", "max_connections=1000", "-c", "fsync=off")
            .Build();

    /// <summary>
    /// Connection string of the shared PostgreSQL container (pointing at its default database, the one the shared host
    /// uses), so a suite that must not share rows with the others can create its own throw-away database next to it.
    /// </summary>
    internal string ServerConnectionString => _postgres.GetConnectionString();

    /// <summary>Counts commands sent to the shared database; see <see cref="DbCommandCounter"/>.</summary>
    internal DbCommandCounter DbCommands { get; } = new();

    /// <summary>
    /// Login rate limit the shared host runs with. Every request from the in-process
    /// client shares one address, so the shipped 10/minute would throttle the many
    /// logins of the other suites; the rate-limit tests override it on a derived host.
    /// </summary>
    internal const string UnthrottledPermitLimit = "1000000";

    /// <summary>
    /// Error registry budget per fingerprint the shared host runs with. Many tests raise the very same forced failure within
    /// a minute, so the shipped 20/minute would silently stop persisting them; the storm tests use a derived host with a small one.
    /// </summary>
    internal const string UnthrottledErrorBudget = "1000000";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Permit, int Window), WebApplicationFactory<Program>> _rateLimited = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<object>> _namedHosts = new();

    /// <summary>
    /// A derived host (with its log capture, fault injection, ...) built once per name and disposed together with this factory,
    /// so suites that need the same configuration do not each pay for a new host.
    /// </summary>
    internal T GetOrAddHost<T>(string name, Func<T> create) where T : class
        => (T)_namedHosts.GetOrAdd(name, _ => new Lazy<object>(() => create())).Value;

    /// <summary>
    /// A host on the same container and database whose login rate limit is the given
    /// one. Built once per configuration and disposed together with this factory
    /// (derived factories are disposed with their parent).
    /// </summary>
    internal WebApplicationFactory<Program> WithLoginRateLimit(int permitLimit, int windowSeconds)
        => _rateLimited.GetOrAdd((permitLimit, windowSeconds), key => WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Login:PermitLimit", key.Permit.ToString());
            builder.UseSetting("RateLimiting:Login:WindowSeconds", key.Window.ToString());
        }));

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Accessing Services builds the host, which triggers ConfigureWebHost
        // below — the container must already be running so its connection
        // string is available at that point.
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // appsettings.json ships a placeholder signing key that the API
        // refuses to boot with, so every test host must supply a real one.
        builder.UseSetting("Jwt:SigningKey", TestJwtSettings.SigningKey);
        builder.UseSetting("RateLimiting:Login:PermitLimit", UnthrottledPermitLimit);
        builder.UseSetting("RateLimiting:Login:WindowSeconds", "60");
        builder.UseSetting("ErrorLogging:MaxPerFingerprintPerMinute", UnthrottledErrorBudget);
        builder.UseSetting("ErrorLogging:PersistTimeoutSeconds", "20");
        // The tests drive the webhook worker by hand (WebhookEventRunner), so they decide when events are processed; the
        // worker tests that need the real hosted service start a host of their own.
        builder.UseSetting("Webhooks:Processing:Enabled", "false");
        builder.UseSetting("Webhooks:Retention:Enabled", "false");
        // Background jobs of the conversations are driven by hand in the tests that need them (their runners are called directly).
        builder.UseSetting("Conversations:AutoCloseEnabled", "false");
        // Every call from the in-process client shares one address; the webhook rate-limit tests use a derived host with a small budget.
        builder.UseSetting("Webhooks:FailedAuthRateLimitPerMinute", "1000000");
        builder.UseSetting("Webhooks:ChannelRateLimitPerMinute", "1000000");

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString()).AddInterceptors(DbCommands));

            services.AddSingleton<IStartupFilter, TestClientIp.Filter>();
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }
}
