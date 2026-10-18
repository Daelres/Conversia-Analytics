extern alias ApiHost;

using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// The real API, in-process, on a real PostgreSQL (Testcontainers) — the backend every
/// Web host in these tests talks to. One container per test run (shared fixture).
/// The opt-in demo data is enabled and seeded once (<see cref="DemoAccounts"/>): the demo tenants are fixed, well-known
/// rows that tests only ever read, while every mutating test registers a tenant of its own.
/// </summary>
public sealed class TestApiFactory : WebApplicationFactory<ApiHost::Program>, IAsyncLifetime
{
    private const string UnthrottledPermitLimit = "1000000";
    private const string SigningKey = "web-integration-tests-only-signing-key-0123456789-abcdefghijklm";

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine")
            // The suites open many hosts and pools at once; the default of 100 connections runs out. fsync off: throw-away data.
            .WithCommand("-c", "max_connections=1000", "-c", "fsync=off")
            .Build();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Permit, int Window), WebApplicationFactory<ApiHost::Program>> _rateLimited = new();

    /// <summary>A host on the same database whose login rate limit is the given one (one shared budget: the Web gives the API no client address).</summary>
    internal WebApplicationFactory<ApiHost::Program> WithLoginRateLimit(int permitLimit, int windowSeconds)
        => _rateLimited.GetOrAdd((permitLimit, windowSeconds), key => WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Login:PermitLimit", key.Permit.ToString());
            builder.UseSetting("RateLimiting:Login:WindowSeconds", key.Window.ToString());
        }));

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WebApplicationFactory<ApiHost::Program>> _withReportKey = new();

    /// <summary>
    /// A host on the same database whose error report endpoint (<c>POST /api/diagnostics/errors</c>) is on, with this shared key, and
    /// neither throttled nor subject to the storm control of the registry (so that the tests of one class, which share this host
    /// and fail at the same few places of the Web, cannot spend each other's budget). Without a key the endpoint is off and answers 404, which is what the plain factory does.
    /// </summary>
    internal WebApplicationFactory<ApiHost::Program> WithErrorReportKey(string key)
        => _withReportKey.GetOrAdd(key, k => WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ErrorLogging:ReportKey", k);
            builder.UseSetting("ErrorLogging:ReportRateLimitPerMinute", "1000000");
            builder.UseSetting("ErrorLogging:MaxPerFingerprintPerMinute", "1000000");
        }));

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Key, string Marker), WebApplicationFactory<ApiHost::Program>> _failingSearch = new();

    /// <summary>
    /// Like <see cref="WithErrorReportKey"/>, and the listing of products fails with an unexpected exception (a real API 500, registered
    /// by the API itself) whenever the search text contains <paramref name="marker"/>; every other request is untouched.
    /// </summary>
    internal WebApplicationFactory<ApiHost::Program> WithFailingProductSearch(string key, string marker)
        => _failingSearch.GetOrAdd((key, marker), k => WithErrorReportKey(k.Key).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => ServiceFaults.Decorate<IProductService>(services, (method, args) =>
                method == nameof(IProductService.ListAsync) && args[0] is Conversia.Application.DTOs.Products.ProductQuery { Search: { } search } && search.Contains(k.Marker, StringComparison.Ordinal)
                    ? new InvalidOperationException($"the product search broke ({search})")
                    : null))));

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Migrated before the host exists, so the API's startup seeding finds its schema; the explicit call below is
        // idempotent and only makes the test fixture independent of that startup behaviour.
        await using (var migrator = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options,
            new NoTenant()))
        {
            await migrator.Database.MigrateAsync();
        }

        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDemoDataSeeder>().SeedAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.UseSetting("RateLimiting:Login:PermitLimit", UnthrottledPermitLimit);
        builder.UseSetting("RateLimiting:Login:WindowSeconds", "60");
        builder.UseSetting("Demo:Enabled", "true");
        builder.UseSetting("Webhooks:Processing:Enabled", "false"); // the Web tests never send a webhook
        builder.UseSetting("Webhooks:Retention:Enabled", "false");
        // Background jobs of the conversations are driven by hand in the tests that need them (their runners are called directly).
        builder.UseSetting("Conversations:AutoCloseEnabled", "false");
        builder.UseSetting("Classification:Enabled", "false");

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString()));
        });
    }

    private sealed class NoTenant : ICurrentTenantService
    {
        public Guid TenantId => Guid.Empty;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }
}

[CollectionDefinition(nameof(WebIntegrationCollection))]
public sealed class WebIntegrationCollection : ICollectionFixture<TestApiFactory>
{
}
