using System.Net;
using System.Net.Http.Json;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.IntegrationTests;

/// <summary>
/// Real concurrency/load tests against the real Testcontainers PostgreSQL —
/// per docs/WORKFLOW.md, Role A must exercise concurrent/parallel users where
/// the feature is concurrency-sensitive (registration racing on a unique
/// email is exactly that).
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class RegisterTenantConcurrencyTests
{
    private readonly CustomWebApplicationFactory _factory;

    public RegisterTenantConcurrencyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RegisterTenant_20ConcurrentRequestsWithSameEmail_ExactlyOneSucceedsAndRestConflict()
    {
        const int concurrentRequests = 20;
        var email = $"race-{Guid.NewGuid():N}@example.test";

        var responses = await Task.WhenAll(Enumerable.Range(0, concurrentRequests).Select(async i =>
        {
            using var client = _factory.CreateClient();
            var payload = new { tenantName = $"Race Co {i}", email, password = "Sup3r-Secret!" };
            try
            {
                return await client.PostAsJsonAsync("/api/auth/register-tenant", payload);
            }
            catch (Exception)
            {
                return null;
            }
        }));

        Assert.All(responses, r => Assert.NotNull(r));

        var successCount = responses.Count(r => r!.StatusCode == HttpStatusCode.Created);
        var conflictCount = responses.Count(r => r!.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, successCount);
        Assert.Equal(concurrentRequests - 1, conflictCount);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // IgnoreQueryFilters: phase 2 added a tenant-scoped global query
        // filter on Users; this assertion checks global persistence state
        // outside any authenticated tenant context.
        var users = await dbContext.Users.IgnoreQueryFilters().Where(u => u.Email == email).ToListAsync();
        Assert.Single(users);
    }

    [Fact]
    public async Task RegisterTenant_50ConcurrentRequestsWithDistinctEmails_AllSucceedAndPersistWithoutUnhandledFailures()
    {
        const int concurrentRequests = 50;
        var emails = Enumerable.Range(0, concurrentRequests)
            .Select(i => $"load-{i}-{Guid.NewGuid():N}@example.test")
            .ToList();

        // The test itself must never throw or hang, even though at RED state
        // every call currently 500s from AuthService's NotImplementedException
        // stub — that is expected and correct for this phase.
        var responses = await Task.WhenAll(emails.Select(async email =>
        {
            using var client = _factory.CreateClient();
            var payload = new { tenantName = $"Load Co {email}", email, password = "Sup3r-Secret!" };
            try
            {
                return await client.PostAsJsonAsync("/api/auth/register-tenant", payload);
            }
            catch (Exception)
            {
                return null;
            }
        }));

        Assert.All(responses, r => Assert.NotNull(r));

        var successCount = responses.Count(r => r!.StatusCode == HttpStatusCode.Created);
        Assert.Equal(concurrentRequests, successCount);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var email in emails)
        {
            var exists = await dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email);
            Assert.True(exists, $"Expected a persisted user for {email}.");
        }
    }
}
