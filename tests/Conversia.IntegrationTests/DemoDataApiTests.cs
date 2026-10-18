using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Products;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The seeded demo accounts behave as the template promises when used through the real API: they sign in with the
/// demo password and the right claims, each account can do exactly what its permissions allow, and the two demo
/// tenants never see each other. Everything here only reads (or is refused and leaves the data unchanged), so the
/// whole class shares one database seeded by the API's own startup and one sign-in per account;
/// <see cref="DemoDataApiWriteTests"/> covers what changes data.
/// </summary>
public class DemoDataApiTests(CustomWebApplicationFactory container, DemoClassState state) : DemoDataTestBase(container, state)
{
    protected override bool ResetsDatabaseBetweenTests => false;

    private Task<DemoWorld> WorldAsync() => SharedAsync("world", async () =>
    {
        var host = await StartSharedSeededHostAsync();
        return await DemoWorld.SignInEveryoneAsync(host);
    });

    // ---- Signing in ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_WithTheDemoPassword_IssuesTokensForTheRightUserTenantAndOwnership()
    {
        var world = await WorldAsync();
        var expected = new[]
        {
            (DemoData.AcmeOwnerEmail, DemoData.AcmeTenantName, true),
            (DemoData.AcmeManagerEmail, DemoData.AcmeTenantName, false),
            (DemoData.AcmeViewerEmail, DemoData.AcmeTenantName, false),
            (DemoData.AcmeNoAccessEmail, DemoData.AcmeTenantName, false),
            (DemoData.GlobexOwnerEmail, DemoData.GlobexTenantName, true),
        };

        foreach (var (email, tenantName, isOwner) in expected)
        {
            var user = await DemoDbReader.UserAsync(Db, email);
            var tenantId = await DemoDbReader.TenantIdAsync(Db, tenantName);

            using var session = await world.Host.SignInAsync(email);

            Assert.True(
                (user.Id, tenantId, isOwner) == (session.Auth.UserId, session.Auth.TenantId, session.Auth.IsOwner),
                $"{email}: the login response names the wrong user, tenant or ownership.");
            Assert.True((user.Id, tenantId, isOwner) == DecodeClaims(session.Auth.Token), $"{email}: the token carries the wrong claims.");
        }
    }

    [Fact]
    public async Task Login_WithAnyOtherPassword_IsRefusedWithTheGenericUnauthorized()
    {
        var world = await WorldAsync();

        foreach (var email in DemoData.AllEmails)
        {
            using (var accepted = await world.Host.LoginRawAsync(email))
            {
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            }

            foreach (var wrong in new[] { StrongPassword, "demo-pass-2026!", "Demo-Pass-2026", "Demo-Pass-2026!!" })
            {
                using var response = await world.Host.LoginRawAsync(email, wrong);
                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{email} with '{wrong}' got {response.StatusCode}.");
            }
        }
    }

    [Fact]
    public async Task Profile_OfEachAcmeAccount_ListsExactlyItsDemoPermissions()
    {
        var world = await WorldAsync();
        var expected = new (DemoSession Session, string Email, Permission[] Permissions)[]
        {
            (world.Owner, DemoData.AcmeOwnerEmail, Array.Empty<Permission>()),
            (world.Manager, DemoData.AcmeManagerEmail, DemoData.ManagerPermissions),
            (world.Viewer, DemoData.AcmeViewerEmail, DemoData.ViewerPermissions),
            (world.NoAccess, DemoData.AcmeNoAccessEmail, Array.Empty<Permission>()),
        };

        foreach (var (session, email, permissions) in expected)
        {
            var response = await session.Client.GetAsync("/api/users/me");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var profile = (await response.Content.ReadFromJsonAsync<UserProfileResponse>())!;
            Assert.Equal(session.Auth.UserId, profile.UserId);
            Assert.Equal(session.Auth.TenantId, profile.TenantId);
            Assert.True(email == DemoData.AcmeOwnerEmail == profile.IsOwner, $"{email}: wrong IsOwner.");
            // An owner needs no permission rows (the owner bypass); the other accounts hold exactly the demo grants.
            if (!profile.IsOwner)
            {
                Assert.Equal(permissions.Order(), profile.Permissions.Order());
            }
        }
    }

    // ---- viewer: ProductsView only -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Viewer_CanReadTheThirteenAcmeProducts_ButEveryWriteIsForbiddenAndChangesNothing()
    {
        var world = await WorldAsync();
        var viewer = world.Viewer;
        var products = await DemoApi.AllProductsAsync(viewer.Client);
        Assert.Equal(DemoData.AcmeProductCount, products.Count);
        var target = products[0];
        await GetProductOkAsync(viewer.Client, target.Id);
        var before = await DemoDbReader.SnapshotAsync(Db);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostProductAsync(viewer.Client, "Sneaky", UniqueSku("v"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutProductAsync(viewer.Client, target.Id, "Sneaky", 1m, 1, target.RowVersion)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteProductAsync(viewer.Client, target.Id)).StatusCode);

        Assert.Equal(before, await DemoDbReader.SnapshotAsync(Db));
    }

    [Fact]
    public async Task Viewer_CannotListUsersOrManageThem()
    {
        var world = await WorldAsync();
        var manager = world.Manager;
        var viewer = world.Viewer;
        var before = await DemoDbReader.SnapshotAsync(Db);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateUserAsync(viewer.Client, UniqueEmail("v"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.Client.PostAsJsonAsync($"/api/users/{manager.Auth.UserId}/permissions", new { permission = (int)Permission.UsersManage })).StatusCode);

        Assert.Equal(before, await DemoDbReader.SnapshotAsync(Db));
    }

    // ---- noaccess: no permissions ------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoAccess_IsAuthenticatedButForbiddenFromProductsAndUsersAlike()
    {
        var world = await WorldAsync();
        var noAccess = world.NoAccess;
        var anyProduct = (await DemoDbReader.ProductsAsync(Db)).First();
        var before = await DemoDbReader.SnapshotAsync(Db);

        Assert.Equal(HttpStatusCode.OK, (await noAccess.Client.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ListProductsAsync(noAccess.Client)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetProductAsync(noAccess.Client, anyProduct.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostProductAsync(noAccess.Client, "Nope", UniqueSku("n"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteProductAsync(noAccess.Client, anyProduct.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noAccess.Client.GetAsync("/api/users")).StatusCode);

        Assert.Equal(before, await DemoDbReader.SnapshotAsync(Db));
    }

    [Fact]
    public async Task Manager_CanListTheFourAcmeUsersAndNobodyElse()
    {
        var world = await WorldAsync();
        var manager = world.Manager;

        var users = await ListUsersAsync(manager.Client);

        Assert.Equal(DemoData.AcmeEmails.Order(), users.Select(u => u.Email).Order());
        Assert.Equal(new[] { DemoData.AcmeOwnerEmail }, users.Where(u => u.IsOwner).Select(u => u.Email));
    }

    [Fact]
    public async Task Manager_CannotAssignRevokeOrCreateUsers_BecauseItLacksUsersManage()
    {
        var world = await WorldAsync();
        var manager = world.Manager;
        var viewer = world.Viewer;
        var before = await DemoDbReader.SnapshotAsync(Db);

        var grant = await manager.Client.PostAsJsonAsync($"/api/users/{viewer.Auth.UserId}/permissions", new { permission = (int)Permission.ProductsManage });
        var grantSelf = await manager.Client.PostAsJsonAsync($"/api/users/{manager.Auth.UserId}/permissions", new { permission = (int)Permission.UsersManage });
        var revoke = await RevokeAsync(manager.Client, viewer.Auth.UserId, Permission.ProductsView);
        var create = await CreateUserAsync(manager.Client, UniqueEmail("by-manager"));

        Assert.Equal(
            new[] { HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden },
            new[] { grant.StatusCode, grantSelf.StatusCode, revoke.StatusCode, create.StatusCode });
        Assert.Equal(before, await DemoDbReader.SnapshotAsync(Db));
    }

    // ---- SKUs are unique per tenant ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSharedSku_ExistsInBothTenants_AndIsTakenInEach()
    {
        var world = await WorldAsync();
        var owner = world.Owner;
        var globex = world.Globex;

        var acme = (await DemoApi.AllProductsAsync(owner.Client)).Single(p => p.Sku == DemoData.SharedSku);
        var other = (await DemoApi.AllProductsAsync(globex.Client)).Single(p => p.Sku == DemoData.SharedSku);

        Assert.NotEqual(acme.Id, other.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await PostProductAsync(owner.Client, "Duplicate", DemoData.SharedSku)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostProductAsync(globex.Client, "Duplicate", DemoData.SharedSku)).StatusCode);
    }

    // ---- The two demo tenants never see each other ------------------------------------------------------------------------

    [Fact]
    public async Task EachTenantsList_ContainsOnlyItsOwnProducts()
    {
        var world = await WorldAsync();
        var owner = world.Owner;
        var manager = world.Manager;
        var viewer = world.Viewer;
        var globex = world.Globex;
        var acmeId = await DemoDbReader.TenantIdAsync(Db, DemoData.AcmeTenantName);
        var globexId = await DemoDbReader.TenantIdAsync(Db, DemoData.GlobexTenantName);
        var acmeIds = (await DemoDbReader.ProductsAsync(Db, acmeId)).Select(p => p.Id).Order().ToList();
        var globexIds = (await DemoDbReader.ProductsAsync(Db, globexId)).Select(p => p.Id).Order().ToList();

        foreach (var acmeUser in new[] { owner, manager, viewer })
        {
            Assert.Equal(acmeIds, (await DemoApi.AllProductsAsync(acmeUser.Client)).Select(p => p.Id).Order());
        }

        Assert.Equal(globexIds, (await DemoApi.AllProductsAsync(globex.Client)).Select(p => p.Id).Order());
        Assert.Equal(DemoData.GlobexProductCount, (await ListProductsOkAsync(globex.Client)).TotalCount);
    }

    [Fact]
    public async Task GlobexOwner_GetsNotFoundForEveryAcmeProduct_AndCannotChangeOrDeleteOne()
    {
        var world = await WorldAsync();
        var owner = world.Owner;
        var globex = world.Globex;
        var acmeProducts = await DemoApi.AllProductsAsync(owner.Client);
        var before = await DemoDbReader.SnapshotAsync(Db);

        foreach (var product in acmeProducts)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await GetProductAsync(globex.Client, product.Id)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await PutProductAsync(globex.Client, product.Id, "Hijacked", 1m, 1, product.RowVersion)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await DeleteProductAsync(globex.Client, product.Id)).StatusCode);
        }

        Assert.Equal(before, await DemoDbReader.SnapshotAsync(Db));
    }

    [Fact]
    public async Task AcmeAccounts_GetNotFoundForEveryGlobexProduct()
    {
        var world = await WorldAsync();
        var owner = world.Owner;
        var manager = world.Manager;
        var viewer = world.Viewer;
        var globex = world.Globex;
        var globexProducts = await DemoApi.AllProductsAsync(globex.Client);
        var before = await DemoDbReader.SnapshotAsync(Db);

        foreach (var product in globexProducts)
        {
            foreach (var acmeUser in new[] { owner, manager, viewer })
            {
                Assert.Equal(HttpStatusCode.NotFound, (await GetProductAsync(acmeUser.Client, product.Id)).StatusCode);
            }

            Assert.Equal(HttpStatusCode.NotFound, (await DeleteProductAsync(owner.Client, product.Id)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await PutProductAsync(manager.Client, product.Id, "Hijacked", 1m, 1, product.RowVersion)).StatusCode);
        }

        Assert.Equal(before, await DemoDbReader.SnapshotAsync(Db));
    }

    [Fact]
    public async Task Users_AreScopedToTheirTenant_AndGlobexCannotTouchAcmeAccounts()
    {
        var world = await WorldAsync();
        var owner = world.Owner;
        var viewer = world.Viewer;
        var globex = world.Globex;

        var globexUsers = await ListUsersAsync(globex.Client);
        var acmeUsers = await ListUsersAsync(owner.Client);

        Assert.Equal(new[] { DemoData.GlobexOwnerEmail }, globexUsers.Select(u => u.Email));
        Assert.DoesNotContain(acmeUsers, u => u.Email == DemoData.GlobexOwnerEmail);
        var before = await DemoDbReader.SnapshotAsync(Db);

        var grant = await globex.Client.PostAsJsonAsync($"/api/users/{viewer.Auth.UserId}/permissions", new { permission = (int)Permission.UsersManage });
        var revoke = await RevokeAsync(globex.Client, viewer.Auth.UserId, Permission.ProductsView);
        var duplicate = await CreateUserAsync(globex.Client, DemoData.AcmeViewerEmail);

        Assert.Equal(HttpStatusCode.NotFound, grant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(before, await DemoDbReader.SnapshotAsync(Db));
    }
}
