using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Users;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.IntegrationTests;

/// <summary>Helpers shared by the integration test classes.</summary>
internal static partial class TestHelpers
{
    public const string StrongPassword = "Sup3r-Secret!";

    public static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.test";

    public static string UniqueTenantName(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    /// <summary>
    /// Registers a new tenant through the real endpoint and returns the owner's
    /// user id and the tenant id read from the issued token.
    /// </summary>
    public static async Task<(Guid UserId, Guid TenantId)> RegisterOwnerAsync(HttpClient client, string tenantPrefix)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register-tenant", new
        {
            tenantName = UniqueTenantName(tenantPrefix),
            email = UniqueEmail("owner"),
            password = StrongPassword
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var token = (await response.Content.ReadFromJsonAsync<AuthResponse>())!.Token;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        return (
            Guid.Parse(jwt.Claims.First(c => c.Type is "sub" or JwtRegisteredClaimNames.Sub).Value),
            Guid.Parse(jwt.Claims.First(c => c.Type == "tenant_id").Value));
    }

    internal sealed record TenantSession(Guid TenantId, Guid OwnerId, string OwnerEmail, string OwnerToken);

    internal sealed record MemberSession(Guid UserId, string Email, string Token);

    public static (Guid UserId, Guid TenantId, bool IsOwner) DecodeClaims(string token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        return (
            Guid.Parse(jwt.Claims.First(c => c.Type is "sub" or JwtRegisteredClaimNames.Sub).Value),
            Guid.Parse(jwt.Claims.First(c => c.Type == "tenant_id").Value),
            bool.Parse(jwt.Claims.First(c => c.Type == "is_owner").Value));
    }

    /// <summary>
    /// Registers a tenant through the real endpoint and keeps the owner's token. <paramref name="pending"/> keeps the tenant as it
    /// registers (Pending, quota 0), for the tests of the approval flow; any other test wants a tenant that operates.
    /// </summary>
    internal static async Task<TenantSession> RegisterTenantSessionAsync(HttpClient client, string prefix = "owner", bool pending = false)
    {
        var email = UniqueEmail(prefix);
        var response = await client.PostAsJsonAsync("/api/auth/register-tenant", new
        {
            tenantName = (pending ? PendingTenantPrefix : string.Empty) + UniqueTenantName(prefix),
            email,
            password = StrongPassword
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var token = (await response.Content.ReadFromJsonAsync<AuthResponse>())!.Token;
        var (userId, tenantId, _) = DecodeClaims(token);
        return new TenantSession(tenantId, userId, email, token);
    }

    public static HttpClient AuthorizedClient(CustomWebApplicationFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<string> LoginAsync(HttpClient client, string email, string password = StrongPassword)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!.Token;
    }

    /// <summary>
    /// Seeds a non-owner user straight through the repository (independent of
    /// <c>POST /api/users</c>, so tests of other endpoints do not depend on it)
    /// and logs them in through the real login endpoint.
    /// </summary>
    internal static async Task<MemberSession> SeedMemberAsync(
        CustomWebApplicationFactory factory, HttpClient client, Guid tenantId, string prefix = "member")
    {
        var email = UniqueEmail(prefix);
        using (var scope = factory.Services.CreateScope())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();
            var user = User.Create(tenantId, email, hasher.HashPassword(StrongPassword), isOwner: false);
            await scope.ServiceProvider.GetRequiredService<IUserRepository>().AddAsync(user);
        }

        var token = await LoginAsync(client, email);
        return new MemberSession(DecodeClaims(token).UserId, email, token);
    }

    /// <summary>Grants a permission through the existing assignment endpoint.</summary>
    public static async Task GrantAsync(HttpClient callerClient, Guid userId, Permission permission)
    {
        var response = await callerClient.PostAsJsonAsync(
            $"/api/users/{userId}/permissions", new { permission = (int)permission });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public static Task<HttpResponseMessage> CreateUserAsync(HttpClient callerClient, string email, string password = StrongPassword)
        => callerClient.PostAsJsonAsync("/api/users", new { email, password });

    /// <param name="permission">Route segment as sent on the wire: a name or a number.</param>
    public static Task<HttpResponseMessage> RevokeAsync(HttpClient callerClient, Guid userId, string permission)
        => callerClient.DeleteAsync($"/api/users/{userId}/permissions/{Uri.EscapeDataString(permission)}");

    public static Task<HttpResponseMessage> RevokeAsync(HttpClient callerClient, Guid userId, Permission permission)
        => RevokeAsync(callerClient, userId, permission.ToString());

    /// <summary>
    /// Runs all calls at once and returns each call's status code, in call
    /// order. Every response is disposed here (callers only ever need the
    /// status); a transport-level failure becomes a null entry the tests reject.
    /// </summary>
    public static async Task<HttpStatusCode?[]> RunInParallelAsync(IEnumerable<Func<Task<HttpResponseMessage>>> calls)
        => await Task.WhenAll(calls.Select(async call =>
        {
            try
            {
                using var response = await call();
                return (HttpStatusCode?)response.StatusCode;
            }
            catch (Exception)
            {
                return null;
            }
        }));

    /// <summary>Reads persisted rows across all tenants, bypassing the tenant query filter.</summary>
    public static async Task<List<User>> FindUsersByEmailAsync(CustomWebApplicationFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Email == email).ToListAsync();
    }

    public static async Task<List<Permission>> PersistedPermissionsAsync(CustomWebApplicationFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.UserPermissions.IgnoreQueryFilters()
            .Where(up => up.UserId == userId)
            .Select(up => up.Permission)
            .ToListAsync();
    }

    public static async Task<ProblemDetails> ReadProblemJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        return problem!;
    }

    public static async Task<IReadOnlyList<UserSummaryResponse>> ListUsersAsync(HttpClient callerClient)
    {
        var response = await callerClient.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<UserSummaryResponse>>())!;
    }

    /// <summary>Status of <c>GET /api/users</c> — the probe endpoint guarded by <see cref="Permission.UsersView"/>.</summary>
    public static async Task<HttpStatusCode> ProbeUsersViewAsync(CustomWebApplicationFactory factory, string token)
    {
        using var client = AuthorizedClient(factory, token);
        return (await client.GetAsync("/api/users")).StatusCode;
    }
}
