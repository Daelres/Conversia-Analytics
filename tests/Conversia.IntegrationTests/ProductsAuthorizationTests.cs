using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Who may do what on <c>/api/products</c>: 401 for anonymous callers, 403 for authenticated callers without the
/// permission named after the policy (<c>ProductsView</c> for reads, <c>ProductsManage</c> for writes), owner bypass,
/// and grants/revocations taking effect on the very next request. Each refusal is paired with a success of the same
/// caller once the right permission is granted (or of the owner for an anonymous caller), so a missing route or an
/// endpoint that refuses everyone cannot satisfy these tests.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ProductsAuthorizationTests
{
    public enum Op { List, Get, Create, Update, Delete }

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProductsAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static readonly Op[] ReadOps = { Op.List, Op.Get };
    private static readonly Op[] WriteOps = { Op.Create, Op.Update, Op.Delete };

    private static Permission RequiredFor(Op op) => ReadOps.Contains(op) ? Permission.ProductsView : Permission.ProductsManage;

    private static HttpStatusCode SuccessFor(Op op) => op switch
    {
        Op.Create => HttpStatusCode.Created,
        Op.Delete => HttpStatusCode.NoContent,
        _ => HttpStatusCode.OK,
    };

    /// <summary>The products each operation targets: one per operation, so a successful call never disturbs another.</summary>
    private sealed record Targets(Conversia.Application.DTOs.Products.ProductResponse ForGet, Conversia.Application.DTOs.Products.ProductResponse ForUpdate, Conversia.Application.DTOs.Products.ProductResponse ForDelete, string NewSku);

    private static async Task<Targets> SeedTargetsAsync(HttpClient ownerClient)
        => new(await CreateProductAsync(ownerClient), await CreateProductAsync(ownerClient), await CreateProductAsync(ownerClient), UniqueSku("new"));

    private static Task<HttpResponseMessage> RunAsync(HttpClient client, Op op, Targets t) => op switch
    {
        Op.List => ListProductsAsync(client),
        Op.Get => GetProductAsync(client, t.ForGet.Id),
        Op.Create => PostProductAsync(client, "Created by actor", t.NewSku, 1m, 1),
        Op.Update => PutProductAsync(client, t.ForUpdate.Id, "Edited by actor", 2m, 2, t.ForUpdate.RowVersion),
        _ => DeleteProductAsync(client, t.ForDelete.Id),
    };

    private async Task AssertNothingWrittenAsync(HttpClient ownerClient, Targets t)
    {
        await AssertProductUnchangedAsync(ownerClient, t.ForUpdate);
        await AssertProductUnchangedAsync(ownerClient, t.ForDelete);
        Assert.Empty(await FindProductsBySkuAsync(_factory, t.NewSku));
        Assert.Equal(3, (await ListProductsOkAsync(ownerClient)).TotalCount);
    }

    // ---- The permission matrix -------------------------------------------------------------

    public static TheoryData<string, Permission[]> MembersByPermissions => new()
    {
        { "no permissions", Array.Empty<Permission>() },
        { "UsersView only", new[] { Permission.UsersView } },
        { "UsersManage only", new[] { Permission.UsersManage } },
        { "UsersView and UsersManage", new[] { Permission.UsersView, Permission.UsersManage } },
        { "ProductsView only", new[] { Permission.ProductsView } },
        { "ProductsManage only", new[] { Permission.ProductsManage } },
    };

    [Theory]
    [MemberData(nameof(MembersByPermissions))]
    public async Task Member_CanDoExactlyWhatTheirProductsPermissionsAllow_AndEachRefusalLiftsOnceGranted(
        string _, Permission[] held)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client);
        var owner = tenant.Owner.Client;
        var targets = await SeedTargetsAsync(owner);
        using var member = await tenant.AddMemberAsync("member", held);

        var refused = new List<Op>();
        foreach (var op in Enum.GetValues<Op>())
        {
            var allowed = held.Contains(RequiredFor(op));
            if (!allowed)
            {
                refused.Add(op);
                var response = await RunAsync(member.Client, op, targets);
                Assert.True(
                    response.StatusCode == HttpStatusCode.Forbidden,
                    $"{op} by [{string.Join(", ", held)}] should be 403 but was {(int)response.StatusCode}.");
            }
        }

        await AssertNothingWrittenAsync(owner, targets);

        // Allowed operations really work for this caller (and some, like Delete, change state: run them after the refusals).
        foreach (var op in Enum.GetValues<Op>().Where(op => held.Contains(RequiredFor(op)) && op != Op.Delete))
        {
            Assert.Equal(SuccessFor(op), (await RunAsync(member.Client, op, targets)).StatusCode);
        }

        // The refusals were about the missing permission only: grant it and the very same calls succeed.
        foreach (var missing in refused.Select(RequiredFor).Distinct())
        {
            await GrantAsync(owner, member.UserId, missing);
        }

        var fresh = await SeedTargetsAsync(owner);
        foreach (var op in refused)
        {
            var response = await RunAsync(member.Client, op, fresh);
            Assert.True(
                response.StatusCode == SuccessFor(op),
                $"{op} should succeed after granting {RequiredFor(op)} but was {(int)response.StatusCode}.");
        }
    }

    [Fact]
    public async Task Owner_CanDoEverythingWithoutAnyExplicitPermissionRow()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client);
        var owner = tenant.Owner.Client;
        var targets = await SeedTargetsAsync(owner);

        foreach (var op in Enum.GetValues<Op>())
        {
            Assert.Equal(SuccessFor(op), (await RunAsync(owner, op, targets)).StatusCode);
        }

        Assert.Empty(await PersistedPermissionsAsync(_factory, tenant.Owner.UserId));
    }

    [Fact]
    public async Task Member_WithBothProductsPermissions_CanDoEverything()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client);
        var targets = await SeedTargetsAsync(tenant.Owner.Client);
        using var member = await tenant.AddMemberAsync("both", Permission.ProductsView, Permission.ProductsManage);

        foreach (var op in Enum.GetValues<Op>())
        {
            Assert.Equal(SuccessFor(op), (await RunAsync(member.Client, op, targets)).StatusCode);
        }
    }

    public static TheoryData<string> UnauthenticatedCallers => new() { "no Authorization header", "garbage bearer token", "empty bearer token" };

    [Theory]
    [MemberData(nameof(UnauthenticatedCallers))]
    public async Task Unauthenticated_EveryOperationIs401_WhileTheOwnerSucceedsOnTheSameCalls(string kind)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client);
        var owner = tenant.Owner.Client;
        var targets = await SeedTargetsAsync(owner);
        using var anonymous = _factory.CreateClient();
        if (kind == "garbage bearer token")
        {
            anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");
        }
        else if (kind == "empty bearer token")
        {
            anonymous.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");
        }

        foreach (var op in Enum.GetValues<Op>())
        {
            var response = await RunAsync(anonymous, op, targets);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{op} should be 401 but was {(int)response.StatusCode}.");
        }

        await AssertNothingWrittenAsync(owner, targets);
        foreach (var op in Enum.GetValues<Op>())
        {
            Assert.Equal(SuccessFor(op), (await RunAsync(owner, op, targets)).StatusCode);
        }
    }

    [Fact]
    public async Task Authorization_IsDecidedBeforeTheTargetIsLookedUpAndBeforeTheBodyIsValidated()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client);
        var real = await CreateProductAsync(tenant.Owner.Client);
        using var viewer = await tenant.AddMemberAsync("viewer", Permission.ProductsView);
        using var usersAdmin = await tenant.AddMemberAsync("usersAdmin", Permission.UsersManage);
        var unknownId = Guid.NewGuid();

        // The viewer reads fine, and tells a real id (200) from an unknown one (404) ...
        Assert.Equal(HttpStatusCode.OK, (await GetProductAsync(viewer.Client, real.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetProductAsync(viewer.Client, unknownId)).StatusCode);

        // ... but for writes it is 403 whether the id exists or not, and whether the body is valid or not.
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteProductAsync(viewer.Client, unknownId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteProductAsync(viewer.Client, real.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutProductAsync(viewer.Client, unknownId, "", -1m, -1, "garbage")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostProductAsync(viewer.Client, "", "bad sku", -1m, -1)).StatusCode);

        // A caller without ProductsView cannot even learn whether an id exists.
        Assert.Equal(HttpStatusCode.Forbidden, (await GetProductAsync(usersAdmin.Client, unknownId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetProductAsync(usersAdmin.Client, real.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ListProductsAsync(usersAdmin.Client, "page=0")).StatusCode);

        await AssertProductUnchangedAsync(tenant.Owner.Client, real);
    }

    [Fact]
    public async Task UsersManageOnly_StillAdministersUsers_ButHasNoAccessToProducts()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client);
        using var usersAdmin = await tenant.AddMemberAsync("usersAdmin", Permission.UsersManage);

        var createdUser = await CreateUserAsync(usersAdmin.Client, UniqueEmail("by-users-admin"));
        var list = await ListProductsAsync(usersAdmin.Client);
        var create = await PostProductAsync(usersAdmin.Client, "X", UniqueSku());

        Assert.Equal(HttpStatusCode.Created, createdUser.StatusCode); // the permission is real ...
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);      // ... it just does not extend to products
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    // ---- Grants and revocations --------------------------------------------------------------

    [Theory]
    [InlineData(Permission.ProductsView, 3)]
    [InlineData(Permission.ProductsManage, 4)]
    public async Task ProductsPermissions_CanBeGrantedByNumberThroughTheAssignmentEndpoint_AndAppearInTheProfile(Permission permission, int number)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client);
        var member = await SeedMemberAsync(_factory, _client, tenant.TenantId);

        var response = await tenant.Owner.Client.PostAsJsonAsync($"/api/users/{member.UserId}/permissions", new { permission = number });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new[] { permission }, await PersistedPermissionsAsync(_factory, member.UserId));
        using var memberClient = AuthorizedClient(_factory, member.Token);
        var profile = await memberClient.GetFromJsonAsync<UserProfileResponse>("/api/users/me");
        Assert.Equal(new[] { permission }, profile!.Permissions);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(99)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GrantingAnUndefinedPermission_IsA400AndGrantsNothing(int number)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client);
        var member = await SeedMemberAsync(_factory, _client, tenant.TenantId);

        var response = await tenant.Owner.Client.PostAsJsonAsync($"/api/users/{member.UserId}/permissions", new { permission = number });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await PersistedPermissionsAsync(_factory, member.UserId));
        await GrantAsync(tenant.Owner.Client, member.UserId, Permission.ProductsView); // the endpoint itself is fine
    }

    [Theory]
    [InlineData("ProductsView", Permission.ProductsView)]
    [InlineData("productsview", Permission.ProductsView)]
    [InlineData("3", Permission.ProductsView)]
    [InlineData("ProductsManage", Permission.ProductsManage)]
    [InlineData("4", Permission.ProductsManage)]
    public async Task RevokingAProductsPermission_TakesEffectOnTheMembersVeryNextRequest(string spelling, Permission permission)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client);
        var targets = await SeedTargetsAsync(tenant.Owner.Client);
        using var member = await tenant.AddMemberAsync("member", permission);
        var probe = permission == Permission.ProductsView ? Op.List : Op.Create;
        Assert.Equal(SuccessFor(probe), (await RunAsync(member.Client, probe, targets)).StatusCode);

        var revoked = await RevokeAsync(tenant.Owner.Client, member.UserId, spelling);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RunAsync(member.Client, probe, targets)).StatusCode); // same token, no re-login
    }

    [Fact]
    public async Task RevokingOneProductsPermission_LeavesTheOtherIntact()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client);
        var targets = await SeedTargetsAsync(tenant.Owner.Client);
        using var member = await tenant.AddMemberAsync("member", Permission.ProductsView, Permission.ProductsManage);

        Assert.Equal(HttpStatusCode.NoContent, (await RevokeAsync(tenant.Owner.Client, member.UserId, Permission.ProductsManage)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await RunAsync(member.Client, Op.List, targets)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RunAsync(member.Client, Op.Create, targets)).StatusCode);
    }

    [Fact]
    public async Task AGrantInOneTenant_GivesNoAccessToProductsOfAnotherTenant()
    {
        using var tenantA = await StartProductTenantAsync(_factory, _client, "a");
        using var tenantB = await StartProductTenantAsync(_factory, _client, "b");
        var inB = await CreateProductAsync(tenantB.Owner.Client);
        using var viewerA = await tenantA.AddMemberAsync("viewerA", Permission.ProductsView, Permission.ProductsManage);
        var inA = await CreateProductAsync(viewerA.Client);

        Assert.Equal(HttpStatusCode.OK, (await GetProductAsync(viewerA.Client, inA.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetProductAsync(viewerA.Client, inB.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteProductAsync(viewerA.Client, inB.Id)).StatusCode);
        await AssertProductUnchangedAsync(tenantB.Owner.Client, inB);
    }
}
