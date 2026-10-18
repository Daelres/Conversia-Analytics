using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>Arranges data by talking to the API (or its database) directly, bypassing the Web under test.</summary>
internal static class ApiSeed
{
    public const string StrongPassword = "Sup3r-Secret!";

    public sealed record Owner(Guid TenantId, Guid UserId, string Email, string Token);

    public sealed record Member(Guid UserId, string Email);

    public static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.test";

    public static string UniqueTenantName(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    public static async Task<Owner> RegisterOwnerAsync(TestApiFactory api, string prefix = "owner")
    {
        using var client = api.CreateClient();
        var email = UniqueEmail(prefix);
        var response = await client.PostAsJsonAsync("/api/auth/register-tenant", new
        {
            tenantName = UniqueTenantName(prefix),
            email,
            password = StrongPassword
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        return new Owner(auth.TenantId, auth.UserId, email, auth.Token);
    }

    public static async Task<Member> CreateMemberAsync(TestApiFactory api, Owner owner, string prefix = "member", params Permission[] permissions)
    {
        using var client = AuthorizedClient(api, owner.Token);
        var email = UniqueEmail(prefix);
        var response = await client.PostAsJsonAsync("/api/users", new { email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<UserSummaryResponse>())!;

        foreach (var permission in permissions)
        {
            await GrantAsync(api, owner, created.UserId, permission);
        }

        return new Member(created.UserId, email);
    }

    public static async Task GrantAsync(TestApiFactory api, Owner owner, Guid userId, Permission permission)
    {
        using var client = AuthorizedClient(api, owner.Token);
        var response = await client.PostAsJsonAsync($"/api/users/{userId}/permissions", new { permission = (int)permission });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public static async Task RevokeAsync(TestApiFactory api, Owner owner, Guid userId, Permission permission)
    {
        using var client = AuthorizedClient(api, owner.Token);
        var response = await client.DeleteAsync($"/api/users/{userId}/permissions/{permission}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public static HttpClient AuthorizedClient(TestApiFactory api, string token)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>What the API itself answers for a request body — the reference for "the Web shows the API's messages".</summary>
    public static async Task<(HttpStatusCode Status, Dictionary<string, string[]> Errors)> PostForValidationAsync(
        TestApiFactory api, string path, object body)
    {
        using var client = api.CreateClient();
        var response = await client.PostAsJsonAsync(path, body);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>();
        return (response.StatusCode, problem?.Errors.ToDictionary(e => e.Key, e => e.Value) ?? new());
    }

    public static DateTime JwtExpiry(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo;

    public static async Task<List<Permission>> PersistedPermissionsAsync(TestApiFactory api, Guid userId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.UserPermissions.IgnoreQueryFilters()
            .Where(up => up.UserId == userId)
            .Select(up => up.Permission)
            .ToListAsync();
    }

    public static async Task<int> CountUsersWithEmailAsync(TestApiFactory api, string email)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.IgnoreQueryFilters().CountAsync(u => u.Email == email);
    }

    public static async Task<int> CountTenantsNamedAsync(TestApiFactory api, string name)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Tenants.IgnoreQueryFilters().CountAsync(t => t.Name == name);
    }

    /// <summary>Inserts a user with an arbitrary stored email (bypassing API validation) to prove output encoding.</summary>
    public static async Task<Guid> SeedUserWithRawEmailAsync(TestApiFactory api, Guid tenantId, string rawEmail)
    {
        using var scope = api.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<Conversia.Application.Interfaces.IPasswordHasherService>();
        var user = Conversia.Domain.Entities.User.Create(tenantId, rawEmail, hasher.HashPassword(StrongPassword), isOwner: false);
        await scope.ServiceProvider.GetRequiredService<Conversia.Domain.Interfaces.IUserRepository>().AddAsync(user);
        return user.Id;
    }
}
