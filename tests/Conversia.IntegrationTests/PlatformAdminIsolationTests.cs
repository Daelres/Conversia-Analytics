using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Conversia.Infrastructure.Administration;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The two kinds of caller never cross: a company's token does nothing on /api/admin, an administrator's token does nothing on any
/// company endpoint, and forging the claims of one kind with the key of the other does not help.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class PlatformAdminIsolationTests
{
    private static readonly JwtOptions Jwt = new() { SigningKey = TestJwtSettings.SigningKey };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PlatformAdminIsolationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // Every company endpoint family, one representative each, plus the ones that need no permission at all.
    public static TheoryData<string> CompanyEndpoints => new()
    {
        "/api/users/me",
        "/api/users",
        "/api/tenant",
        "/api/branches",
        "/api/advisors",
        "/api/channels",
        "/api/conversations",
        "/api/metrics/summary",
    };

    public static TheoryData<string> AdminEndpoints => new()
    {
        "/api/admin/tenants",
        "/api/admin/audit",
    };

    [Theory]
    [MemberData(nameof(CompanyEndpoints))]
    public async Task AnAdminToken_IsRefusedOnEveryCompanyEndpoint(string url)
    {
        using var admin = await StartAdminAsync(_factory, _client);

        var response = await admin.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task ACompanyOwnersToken_AndAMembersToken_AreRefusedOnTheAdminApi(string url)
    {
        using var tenant = await StartTenantAsync(_factory, _client);
        using var member = await tenant.AddMemberAsync("member", Conversia.Domain.Enums.Permission.UsersManage, Conversia.Domain.Enums.Permission.OrganizationManage);

        Assert.Equal(HttpStatusCode.Unauthorized, (await tenant.Owner.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(url)).StatusCode); // no token at all
    }

    [Fact]
    public async Task ACompanyOwner_CannotUseTheAdminWriteEndpoints_EvenForItsOwnCompany()
    {
        using var tenant = await StartTenantAsync(_factory, _client, pending: true);
        var id = tenant.TenantId;

        var activate = await tenant.Owner.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{id}/status", new { status = "Active" });
        var quota = await tenant.Owner.Client.PutAsJsonAsync($"{AdminTenantsUrl}/{id}/line-quota", new { maxActiveLines = 1000 });

        Assert.Equal(HttpStatusCode.Unauthorized, activate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, quota.StatusCode);
        using var admin = await StartAdminAsync(_factory, _client);
        var stored = await GetAdminTenantAsync(admin, id);
        Assert.Equal((Conversia.Domain.Enums.TenantStatus.Pending, 0), (stored.Status, stored.MaxActiveLines));
    }

    [Fact]
    public async Task ATokenSignedWithTheCompaniesKey_ClaimingToBeAnAdmin_IsRefused()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSettings.SigningKey));

        // The right claims and issuer, even the admin audience, but the key of the companies.
        var forged = Mint(key, PlatformAdminTokens.AudienceOf(Jwt), [new Claim("sub", admin.AdminId.ToString()), new Claim("role", "platform_admin")]);
        // The companies' audience with admin claims.
        var forgedAudience = Mint(key, TestJwtSettings.DefaultAudience, [new Claim("sub", admin.AdminId.ToString()), new Claim("role", "platform_admin")]);

        foreach (var token in new[] { forged, forgedAudience })
        {
            using var client = ClientWithToken(_factory, token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/tenants")).StatusCode);
        }
    }

    [Fact]
    public async Task ATokenSignedWithTheAdminKey_IsRefusedOnCompanyEndpoints_EvenWithTenantClaims()
    {
        using var tenant = await StartTenantAsync(_factory, _client);
        using var admin = await StartAdminAsync(_factory, _client);
        var adminKey = PlatformAdminTokens.KeyOf(Jwt);
        var claims = new[]
        {
            new Claim("sub", admin.AdminId.ToString()),
            new Claim("role", "platform_admin"),
            new Claim("tenant_id", tenant.TenantId.ToString()),
            new Claim("is_owner", "true"),
        };

        foreach (var audience in new[] { PlatformAdminTokens.AudienceOf(Jwt), TestJwtSettings.DefaultAudience })
        {
            using var client = ClientWithToken(_factory, Mint(adminKey, audience, claims));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/branches")).StatusCode);
        }
    }

    [Fact]
    public async Task AnAdminTokenThatAlsoCarriesATenant_IsRefusedOnTheAdminApiToo()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        var token = Mint(PlatformAdminTokens.KeyOf(Jwt), PlatformAdminTokens.AudienceOf(Jwt),
            [new Claim("sub", admin.AdminId.ToString()), new Claim("role", "platform_admin"), new Claim("tenant_id", Guid.NewGuid().ToString())]);
        using var client = ClientWithToken(_factory, token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/tenants")).StatusCode);
    }

    [Fact]
    public async Task AValidAdminSignedTokenWithoutTheRole_IsForbidden()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        var token = Mint(PlatformAdminTokens.KeyOf(Jwt), PlatformAdminTokens.AudienceOf(Jwt), [new Claim("sub", admin.AdminId.ToString())]);
        using var client = ClientWithToken(_factory, token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/tenants")).StatusCode);
    }

    [Fact]
    public async Task AnExpiredAdminToken_IsRefused()
    {
        using var admin = await StartAdminAsync(_factory, _client);
        var token = Mint(PlatformAdminTokens.KeyOf(Jwt), PlatformAdminTokens.AudienceOf(Jwt),
            [new Claim("sub", admin.AdminId.ToString()), new Claim("role", "platform_admin")], lifetime: TimeSpan.FromMinutes(-10));
        using var client = ClientWithToken(_factory, token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/tenants")).StatusCode);
    }

    [Fact]
    public async Task ADeactivatedOrDeletedAdmin_LosesAccessAtOnce_NotAtTokenExpiry()
    {
        using var deactivated = await StartAdminAsync(_factory, _client);
        using var deleted = await StartAdminAsync(_factory, _client);
        Assert.Equal(HttpStatusCode.OK, (await deactivated.Client.GetAsync("/api/admin/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await deleted.Client.GetAsync("/api/admin/tenants")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.PlatformAdmins.SingleAsync(a => a.Id == deactivated.AdminId)).Deactivate();
            await db.PlatformAdmins.Where(a => a.Id == deleted.AdminId).ExecuteDeleteAsync();
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await deactivated.Client.GetAsync("/api/admin/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deleted.Client.GetAsync("/api/admin/tenants")).StatusCode);
    }

    [Fact]
    public async Task NoAdminEndpointReturnsConversationsMessagesContactsMetricsOrUsers()
    {
        using var tenant = await StartTenantAsync(_factory, _client);
        using var admin = await StartAdminAsync(_factory, _client);
        var branch = await CreateBranchAsync(tenant.Owner.Client);
        var channel = await CreateChannelAsync(tenant.Owner.Client, branch.Id, "+57 300 555 0111");
        await SeedConversationAsync(_factory.Services, tenant.TenantId, branch.Id, channel.Id, DateTime.UtcNow.AddHours(-2),
            [In(0, "Quiero una cotizacion secreta-del-cliente")], waId: "573009998877");

        var bodies = new List<string>
        {
            await admin.Client.GetStringAsync(AdminTenantsUrl),
            await admin.Client.GetStringAsync($"{AdminTenantsUrl}/{tenant.TenantId}"),
            await admin.Client.GetStringAsync("/api/admin/audit"),
        };

        foreach (var body in bodies)
        {
            Assert.DoesNotContain("secreta-del-cliente", body);
            Assert.DoesNotContain("573009998877", body);
            Assert.DoesNotContain("Cliente", body, StringComparison.Ordinal);
            Assert.DoesNotContain("webhookSecret", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("channelKey", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        }

        // Only the owner's email (a business contact) of the company's users, and the line without its webhook address.
        var detail = await admin.Client.GetStringAsync($"{AdminTenantsUrl}/{tenant.TenantId}");
        Assert.Contains(tenant.Session.OwnerEmail, detail);
        Assert.Contains("+57 300 555 0111", detail);
    }

    private static string Mint(SecurityKey key, string audience, IEnumerable<Claim> claims, TimeSpan? lifetime = null)
    {
        var expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(30));
        var token = new JwtSecurityToken(
            TestJwtSettings.DefaultIssuer, audience, claims,
            notBefore: expires.AddHours(-1), expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
