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

    /// <param name="pending">Leave the company as it registers (Pending): the test host activates every other one (<see cref="Conversia.Tests.Shared.ActivateRegisteredTenantsInterceptor"/>).</param>
    public static async Task<Owner> RegisterOwnerAsync(TestApiFactory api, string prefix = "owner", bool pending = false)
    {
        using var client = api.CreateClient();
        var email = UniqueEmail(prefix);
        var response = await client.PostAsJsonAsync("/api/auth/register-tenant", new
        {
            tenantName = (pending ? Conversia.Tests.Shared.ActivateRegisteredTenantsInterceptor.PendingPrefix : string.Empty) + UniqueTenantName(prefix),
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

    // ---- platform administration (straight at the API: the Web under test never sees these calls) ----

    public const string AdminPassword = "Adm1n-Passw0rd-Test!";

    public sealed record Admin(Guid Id, string Email, string Password);

    /// <summary>Creates a platform administrator in the database (there is no endpoint that does) with a known password.</summary>
    public static async Task<Admin> CreateAdminAsync(TestApiFactory api, string prefix = "admin")
    {
        var email = UniqueEmail(prefix);
        using var scope = api.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<Conversia.Application.Interfaces.IPasswordHasherService>();
        var admin = Conversia.Domain.Entities.PlatformAdmin.Create(email, hasher.HashPassword(AdminPassword));
        Assert.True(await scope.ServiceProvider.GetRequiredService<Conversia.Domain.Interfaces.IPlatformAdminRepository>().TryAddAsync(admin));
        return new Admin(admin.Id, email, AdminPassword);
    }

    /// <summary>An HTTP client to the API that carries the administrator's own token (signed in through the real endpoint).</summary>
    public static async Task<HttpClient> AdminClientAsync(TestApiFactory api, Admin admin)
    {
        using var anonymous = api.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/admin/auth/login", new { email = admin.Email, password = admin.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<Conversia.Application.DTOs.Admin.AdminAuthResponse>())!;
        return AuthorizedClient(api, auth.Token);
    }

    /// <summary>Moves a company to a status through the administrator API.</summary>
    public static async Task SetStatusAsync(TestApiFactory api, Admin admin, Guid tenantId, string status, string? reason = null)
    {
        using var client = await AdminClientAsync(api, admin);
        var response = await client.PutAsJsonAsync($"/api/admin/tenants/{tenantId}/status", new { status, reason });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static async Task SetQuotaAsync(TestApiFactory api, Admin admin, Guid tenantId, int maxActiveLines)
    {
        using var client = await AdminClientAsync(api, admin);
        var response = await client.PutAsJsonAsync($"/api/admin/tenants/{tenantId}/line-quota", new { maxActiveLines });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Registers a branch and as many lines (inactive, as the API creates them) as numbers are given; returns the channel ids in order.</summary>
    public static async Task<IReadOnlyList<Guid>> CreateLinesAsync(TestApiFactory api, Owner owner, params string[] numbers)
    {
        using var client = AuthorizedClient(api, owner.Token);
        var branch = await client.PostAsJsonAsync("/api/branches", new { name = "Centro" });
        Assert.Equal(HttpStatusCode.Created, branch.StatusCode);
        var branchId = (await branch.Content.ReadFromJsonAsync<Conversia.Application.DTOs.Organization.BranchResponse>())!.Id;
        var ids = new List<Guid>();
        foreach (var number in numbers)
        {
            var channel = await client.PostAsJsonAsync("/api/channels", new { branchId, displayPhoneNumber = number });
            Assert.Equal(HttpStatusCode.Created, channel.StatusCode);
            ids.Add((await channel.Content.ReadFromJsonAsync<Conversia.Application.DTOs.Organization.ChannelResponse>())!.Id);
        }

        return ids;
    }

    public static async Task ActivateLineAsync(TestApiFactory api, Owner owner, Guid channelId)
    {
        using var client = AuthorizedClient(api, owner.Token);
        var response = await client.PostAsync($"/api/channels/{channelId}/activate", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
