using System.Net;
using System.Net.Http.Json;
using System.Text;
using Conversia.Application.DTOs.Auth;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

[Collection(nameof(IntegrationTestCollection))]
public class RegisterTenantEndpointTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RegisterTenantEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task RegisterTenant_WithValidInput_Returns201AndPersistsExactlyOneTenantAndOwnerUser()
    {
        var email = UniqueEmail("owner");
        var payload = new
        {
            tenantName = $"Acme Corp {Guid.NewGuid():N}",
            email,
            password = "Sup3r-Secret!"
        };

        var response = await _client.PostAsJsonAsync("/api/auth/register-tenant", payload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // IgnoreQueryFilters: this assertion runs outside any authenticated
        // tenant context (phase 2 added a tenant-scoped global query filter
        // on Users — see docs/USERS_TENANTS_PERMISSIONS.md), so it must look
        // across all tenants to verify global persistence state.
        var users = await dbContext.Users.IgnoreQueryFilters().Where(u => u.Email == email).ToListAsync();
        Assert.Single(users);
        Assert.True(users[0].IsOwner);

        var tenants = await dbContext.Tenants.Where(t => t.Id == users[0].TenantId).ToListAsync();
        Assert.Single(tenants);
    }

    [Fact]
    public async Task RegisterTenant_WithDuplicateEmail_Returns409AndDoesNotDuplicateData()
    {
        var email = UniqueEmail("dup");
        var firstPayload = new { tenantName = "First Co", email, password = "Sup3r-Secret!" };

        var first = await _client.PostAsJsonAsync("/api/auth/register-tenant", firstPayload);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var secondPayload = new { tenantName = "Second Co", email, password = "Another-Secret1!" };
        var second = await _client.PostAsJsonAsync("/api/auth/register-tenant", secondPayload);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = await dbContext.Users.IgnoreQueryFilters().Where(u => u.Email == email).ToListAsync();
        Assert.Single(users);
    }

    [Fact]
    public async Task Email_IsCaseInsensitive_ForTheUniqueIndexAndForLogin()
    {
        // PostgreSQL compares text case-sensitively; the email column is citext to keep the behaviour users expect.
        var email = UniqueEmail("case");
        var first = await _client.PostAsJsonAsync("/api/auth/register-tenant", new { tenantName = "Case Co", email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var sameEmailUpperCased = await _client.PostAsJsonAsync(
            "/api/auth/register-tenant", new { tenantName = "Other Case Co", email = email.ToUpperInvariant(), password = StrongPassword });

        Assert.Equal(HttpStatusCode.Conflict, sameEmailUpperCased.StatusCode);
        Assert.NotEmpty(await LoginAsync(_client, email.ToUpperInvariant()));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await dbContext.Users.IgnoreQueryFilters().CountAsync(u => u.Email == email.ToUpperInvariant()));
    }

    [Fact]
    public async Task RegisterTenant_WithMissingTenantName_Returns400AndWritesNothing()
    {
        var email = UniqueEmail("no-tenant-name");
        var payload = new { tenantName = "", email, password = "Sup3r-Secret!" };

        var response = await _client.PostAsJsonAsync("/api/auth/register-tenant", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email));
    }

    [Fact]
    public async Task RegisterTenant_WithMalformedEmail_Returns400AndWritesNothing()
    {
        const string malformedEmail = "not-an-email";
        var payload = new { tenantName = "Acme Corp", email = malformedEmail, password = "Sup3r-Secret!" };

        var response = await _client.PostAsJsonAsync("/api/auth/register-tenant", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == malformedEmail));
    }

    [Fact]
    public async Task RegisterTenant_WithEmptyPassword_Returns400AndWritesNothing()
    {
        var email = UniqueEmail("no-password");
        var payload = new { tenantName = "Acme Corp", email, password = "" };

        var response = await _client.PostAsJsonAsync("/api/auth/register-tenant", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await dbContext.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email));
    }

    [Fact]
    public async Task RegisterTenant_WithMalformedJsonBody_Returns400NeverAnUnhandledException()
    {
        var content = new StringContent("{ this is not valid json ", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/auth/register-tenant", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterTenant_WithWrongContentType_Returns400Or415NeverAnUnhandledException()
    {
        var content = new StringContent(
            "tenantName=Acme&email=a@a.test&password=x", Encoding.UTF8, "text/plain");

        var response = await _client.PostAsync("/api/auth/register-tenant", content);

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType,
            $"Expected 400 or 415, got {(int)response.StatusCode}.");
    }
}
