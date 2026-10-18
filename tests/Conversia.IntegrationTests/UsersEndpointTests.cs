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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// End-to-end tests for the permission/authorization endpoints
/// (<c>GET /api/users/me</c>, <c>GET /api/users</c>,
/// <c>POST /api/users/{userId}/permissions</c>) against the real
/// Testcontainers PostgreSQL, per docs/USERS_TENANTS_PERMISSIONS.md and
/// docs/WORKFLOW.md.
///
/// There is no API endpoint (yet) to add a second, non-owner user to a
/// tenant — <c>register-tenant</c> only ever creates the owner. To exercise
/// non-owner scenarios these tests seed a second user directly through
/// <see cref="IUserRepository"/> + <see cref="IPasswordHasherService"/>
/// (the same production services the API itself uses), then obtain that
/// user's real JWT through the real <c>/api/auth/login</c> endpoint — the
/// seed is the only shortcut; everything under test still goes through real
/// HTTP + real authentication + real authorization.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class UsersEndpointTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UsersEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private sealed record TenantContext(Guid TenantId, Guid OwnerId, string OwnerEmail, string OwnerPassword, string OwnerToken);

    private async Task<TenantContext> RegisterTenantAsync(string prefix = "owner")
    {
        var email = UniqueEmail(prefix);
        const string password = "Sup3r-Secret!";
        var payload = new { tenantName = $"{prefix} Co {Guid.NewGuid():N}", email, password };

        var response = await _client.PostAsJsonAsync("/api/auth/register-tenant", payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);

        var (userId, tenantId, _) = DecodeClaims(body!.Token);

        return new TenantContext(tenantId, userId, email, password, body.Token);
    }

    private static (Guid UserId, Guid TenantId, bool IsOwner) DecodeClaims(string token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var userId = Guid.Parse(jwt.Claims.First(c => c.Type is "sub" or JwtRegisteredClaimNames.Sub).Value);
        var tenantId = Guid.Parse(jwt.Claims.First(c => c.Type == "tenant_id").Value);
        var isOwner = bool.Parse(jwt.Claims.First(c => c.Type == "is_owner").Value);
        return (userId, tenantId, isOwner);
    }

    /// <summary>
    /// Seeds a non-owner user directly into the tenant via the real
    /// repository + password hasher (no HTTP endpoint exists to create a
    /// second tenant member), then logs them in for real to obtain a real
    /// JWT — see class remarks.
    /// </summary>
    private async Task<(Guid UserId, string Email, string Token)> CreateAndLoginNonOwnerAsync(Guid tenantId, string prefix = "member")
    {
        var email = UniqueEmail(prefix);
        const string password = "Sup3r-Secret!";

        using (var scope = _factory.Services.CreateScope())
        {
            var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();

            var user = User.Create(tenantId, email, passwordHasher.HashPassword(password), isOwner: false);
            await userRepository.AddAsync(user);
        }

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(loginBody);

        var (userId, _, _) = DecodeClaims(loginBody!.Token);
        return (userId, email, loginBody.Token);
    }

    private HttpClient AuthorizedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task GetMe_ReturnsCallersOwnIdentityAndEmptyPermissionsWhenNoneAssigned()
    {
        var owner = await RegisterTenantAsync();
        using var client = AuthorizedClient(owner.OwnerToken);

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.Equal(owner.OwnerId, profile!.UserId);
        Assert.Equal(owner.TenantId, profile.TenantId);
        Assert.True(profile.IsOwner);
        Assert.Empty(profile.Permissions);
    }

    [Fact]
    public async Task Owner_CanListUsersAndAssignPermission_WithNoExplicitUserPermissionsRow()
    {
        var owner = await RegisterTenantAsync();
        using var client = AuthorizedClient(owner.OwnerToken);

        var listResponse = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var users = await listResponse.Content.ReadFromJsonAsync<List<UserSummaryResponse>>();
        Assert.NotNull(users);
        Assert.Contains(users!, u => u.UserId == owner.OwnerId);

        var assignResponse = await client.PostAsJsonAsync(
            $"/api/users/{owner.OwnerId}/permissions",
            new { permission = (int)Permission.UsersView });

        Assert.Equal(HttpStatusCode.NoContent, assignResponse.StatusCode);
    }

    [Fact]
    public async Task NonOwnerWithNoPermissions_Gets403OnListAndAssignEndpoints()
    {
        var owner = await RegisterTenantAsync();
        var member = await CreateAndLoginNonOwnerAsync(owner.TenantId);
        using var client = AuthorizedClient(member.Token);

        var listResponse = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);

        var assignResponse = await client.PostAsJsonAsync(
            $"/api/users/{owner.OwnerId}/permissions",
            new { permission = (int)Permission.UsersView });
        Assert.Equal(HttpStatusCode.Forbidden, assignResponse.StatusCode);
    }

    [Fact]
    public async Task AfterOwnerAssignsPermission_MemberCanCallImmediately_WithoutANewLogin()
    {
        var owner = await RegisterTenantAsync();
        var member = await CreateAndLoginNonOwnerAsync(owner.TenantId);

        using (var memberClientBefore = AuthorizedClient(member.Token))
        {
            var before = await memberClientBefore.GetAsync("/api/users");
            Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        }

        using (var ownerClient = AuthorizedClient(owner.OwnerToken))
        {
            var assignResponse = await ownerClient.PostAsJsonAsync(
                $"/api/users/{member.UserId}/permissions",
                new { permission = (int)Permission.UsersView });
            Assert.Equal(HttpStatusCode.NoContent, assignResponse.StatusCode);
        }

        // Same token as before the assignment — no new login round-trip.
        using var memberClientAfter = AuthorizedClient(member.Token);
        var after = await memberClientAfter.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task TenantIsolation_UsersListNeverLeaksOtherTenantsUsers()
    {
        var tenantA = await RegisterTenantAsync("tenantA");
        var tenantB = await RegisterTenantAsync("tenantB");
        var memberA = await CreateAndLoginNonOwnerAsync(tenantA.TenantId, "memberA");
        var memberB = await CreateAndLoginNonOwnerAsync(tenantB.TenantId, "memberB");

        using var clientA = AuthorizedClient(tenantA.OwnerToken);
        var response = await clientA.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = await response.Content.ReadFromJsonAsync<List<UserSummaryResponse>>();
        Assert.NotNull(users);

        // Real, specific negative assertions against real data belonging to
        // a second real tenant — not an empty-list coincidence.
        Assert.Contains(users!, u => u.UserId == tenantA.OwnerId);
        Assert.Contains(users!, u => u.UserId == memberA.UserId);
        Assert.DoesNotContain(users!, u => u.UserId == tenantB.OwnerId);
        Assert.DoesNotContain(users!, u => u.UserId == memberB.UserId);
        Assert.DoesNotContain(users!, u => u.Email == tenantB.OwnerEmail);
        Assert.DoesNotContain(users!, u => u.Email == memberB.Email);
        Assert.Equal(2, users!.Count);
    }

    [Fact]
    public async Task TenantIsolation_AssigningPermissionToUserInAnotherTenant_FailsInsteadOfSucceeding()
    {
        var tenantA = await RegisterTenantAsync("tenantA");
        var tenantB = await RegisterTenantAsync("tenantB");
        using var clientA = AuthorizedClient(tenantA.OwnerToken);

        // Owner A tries to grant a permission to owner B's user id — a
        // cross-tenant write must never silently succeed.
        var response = await clientA.PostAsJsonAsync(
            $"/api/users/{tenantB.OwnerId}/permissions",
            new { permission = (int)Permission.UsersView });

        // Must be 404 specifically, never 403: a cross-tenant target has to be
        // indistinguishable from a nonexistent one, or the response leaks that
        // the user exists (see UserNotFoundException's anti-enumeration contract).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var leaked = await dbContext.UserPermissions
            .IgnoreQueryFilters()
            .AnyAsync(up => up.UserId == tenantB.OwnerId && up.Permission == Permission.UsersView);
        Assert.False(leaked, "A cross-tenant permission assignment must never be persisted.");
    }

    [Fact]
    public async Task Unauthenticated_Requests_Return401NeverAnUnhandledException()
    {
        var owner = await RegisterTenantAsync();

        var meResponse = await _client.GetAsync("/api/users/me");
        var listResponse = await _client.GetAsync("/api/users");
        var assignResponse = await _client.PostAsJsonAsync(
            $"/api/users/{owner.OwnerId}/permissions",
            new { permission = (int)Permission.UsersView });

        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, assignResponse.StatusCode);
    }

    [Fact]
    public async Task MalformedJwt_Requests_Return401NeverAnUnhandledException()
    {
        var owner = await RegisterTenantAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-jwt");

        var meResponse = await client.GetAsync("/api/users/me");
        var listResponse = await client.GetAsync("/api/users");
        var assignResponse = await client.PostAsJsonAsync(
            $"/api/users/{owner.OwnerId}/permissions",
            new { permission = (int)Permission.UsersView });

        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, assignResponse.StatusCode);
    }

    [Fact]
    public async Task ConcurrentIdenticalPermissionAssignments_NeverDuplicateARowAndNeverUnhandled500()
    {
        var owner = await RegisterTenantAsync();
        var member = await CreateAndLoginNonOwnerAsync(owner.TenantId);

        const int concurrentRequests = 10;
        var responses = await Task.WhenAll(Enumerable.Range(0, concurrentRequests).Select(async _ =>
        {
            using var client = AuthorizedClient(owner.OwnerToken);
            return await client.PostAsJsonAsync(
                $"/api/users/{member.UserId}/permissions",
                new { permission = (int)Permission.UsersView });
        }));

        Assert.All(responses, r => Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode));
        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"Unexpected status {(int)r.StatusCode} for a concurrent identical permission assignment."));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rowCount = await dbContext.UserPermissions
            .IgnoreQueryFilters()
            .CountAsync(up => up.UserId == member.UserId && up.Permission == Permission.UsersView);

        Assert.True(rowCount <= 1, $"Expected at most one UserPermissions row, found {rowCount}.");
    }
}
