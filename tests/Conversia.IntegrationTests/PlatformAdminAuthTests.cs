using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Admin;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.RateLimitTestSupport;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>The platform administrator's own login: its token, its uniform failure, its rate limit.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class PlatformAdminAuthTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PlatformAdminAuthTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Login_IssuesAShortLivedTokenWithTheAdminRoleAndNoTenant()
    {
        using var admin = await StartAdminAsync(_factory, _client);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(admin.Token);

        Assert.Equal(admin.AdminId.ToString(), jwt.Subject);
        Assert.Contains(jwt.Claims, c => c.Type == "role" && c.Value == "platform_admin");
        Assert.DoesNotContain(jwt.Claims, c => c.Type is "tenant_id" or "is_owner");
        Assert.Equal(TestJwtSettings.DefaultAudience + ".platform-admin", Assert.Single(jwt.Audiences)); // not the audience of the companies
        var lifetime = jwt.ValidTo - jwt.ValidFrom;
        Assert.InRange(lifetime.TotalMinutes, 59, 61);
    }

    [Fact]
    public async Task Login_Response_CarriesTheExpiryAndIsNotCacheable()
    {
        using var admin = await StartAdminAsync(_factory, _client);

        var response = await _client.PostAsJsonAsync(AdminLoginUrl, new { email = admin.Email, password = AdminPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await ReadBodyAsync<AdminAuthResponse>(response);
        Assert.Equal(admin.AdminId, body.AdminId);
        Assert.InRange(body.ExpiresAt, DateTime.UtcNow.AddMinutes(58), DateTime.UtcNow.AddMinutes(62));
    }

    [Fact]
    public async Task TheTokenLifetimeIsConfigurable()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var host = _factory.WithWebHostBuilder(b => b.UseSetting("PlatformAdmin:TokenMinutes", "5"));
        using var client = host.CreateClient();

        var response = await client.PostAsJsonAsync(AdminLoginUrl, new { email = admin.Email, password = AdminPassword });

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken((await ReadBodyAsync<AdminAuthResponse>(response)).Token);
        Assert.InRange((jwt.ValidTo - jwt.ValidFrom).TotalMinutes, 4, 6);
    }

    [Fact]
    public async Task EveryFailure_IsTheSame401_SoNothingTellsWhichAdminsExist()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        using var deactivated = await StartAdminAsync(_factory, _client);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.PlatformAdmins.SingleAsync(a => a.Id == deactivated.AdminId);
            row.Deactivate();
            await db.SaveChangesAsync();
        }

        var attempts = new[]
        {
            await _client.PostAsJsonAsync(AdminLoginUrl, new { email = admin.Email, password = "wrong-password-123" }),
            await _client.PostAsJsonAsync(AdminLoginUrl, new { email = UniqueEmail("nobody"), password = AdminPassword }),
            await _client.PostAsJsonAsync(AdminLoginUrl, new { email = deactivated.Email, password = AdminPassword }), // right password, deactivated
        };

        var bodies = new List<string>();
        foreach (var attempt in attempts)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
            Assert.Equal("application/problem+json", attempt.Content.Headers.ContentType?.MediaType);
            bodies.Add(await attempt.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Distinct());
        Assert.Contains("Correo o contraseña incorrectos.", bodies[0]);
    }

    [Fact]
    public async Task ACompanyUsersCredentials_DoNotWorkOnTheAdminLogin_AndViceVersa()
    {
        using var tenant = await StartTenantAsync(_factory, _client);
        using var admin = await StartAdminAsync(_factory, _client);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync(AdminLoginUrl, new { email = tenant.Session.OwnerEmail, password = StrongPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/auth/login", new { email = admin.Email, password = AdminPassword })).StatusCode);
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("", "x")]
    [InlineData("not-an-email", "x")]
    [InlineData("a@example.test", null)]
    [InlineData("a@example.test", "")]
    public async Task Login_WithInvalidInput_Is400WithPerFieldErrors(string? email, string? password)
    {
        var response = await _client.PostAsJsonAsync(AdminLoginUrl, new { email, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_IsRateLimited_WithABudgetOfItsOwn()
    {
        var limited = _factory.WithLoginRateLimit(2, 60); // shared and cached: disposed with the factory
        using var admin = await StartAdminAsync(_factory, _client);
        using var client = ClientFrom(limited, TestClientIp.Next());

        for (var i = 0; i < 2; i++)
        {
            using var wrong = await client.PostAsJsonAsync(AdminLoginUrl, new { email = admin.Email, password = "wrong-password-123" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var throttled = await client.PostAsJsonAsync(AdminLoginUrl, new { email = admin.Email, password = AdminPassword });
        await AssertThrottledAsync(throttled, maxRetryAfterSeconds: 60);

        // The companies' login keeps its own budget for the same address.
        using var tenantLogin = await client.PostAsJsonAsync("/api/auth/login", new { email = UniqueEmail("nobody"), password = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, tenantLogin.StatusCode);
    }
}
