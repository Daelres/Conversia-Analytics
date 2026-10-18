using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Products;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// What the demo accounts can change through the real API, and several of them changing things at once. Every test
/// starts from freshly seeded demo data (the class database is emptied and seeded again before each one) and signs
/// the accounts in again, since reseeding issues new user ids.
/// </summary>
public class DemoDataApiWriteTests(CustomWebApplicationFactory container, DemoClassState state) : DemoDataTestBase(container, state)
{
    private async Task<DemoWorld> FreshWorldAsync()
    {
        var host = await StartEmptyHostAsync();
        await host.SeedAsync();
        var world = await DemoWorld.SignInEveryoneAsync(host);
        _worlds.Add(world);
        return world;
    }

    private readonly List<DemoWorld> _worlds = new();

    public override async Task DisposeAsync()
    {
        _worlds.ForEach(w => w.Dispose());
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Manager_CanCreateUpdateAndDeleteProducts()
    {
        var world = await FreshWorldAsync();
        var manager = world.Manager;

        var created = await CreateProductAsync(manager.Client, "Manager's product", UniqueSku("mgr"), 12.34m, 5);
        var updated = await UpdateProductOkAsync(manager.Client, created, "Manager's product v2", 15m, 2);
        var deleted = await DeleteProductAsync(manager.Client, updated.Id);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetProductAsync(manager.Client, created.Id)).StatusCode);
        Assert.Equal(DemoData.AcmeProductCount, (await DemoApi.AllProductsAsync(manager.Client)).Count);
    }

    // ---- owners ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AcmeOwner_CanDoEverythingInItsOwnTenant()
    {
        var world = await FreshWorldAsync();
        var owner = world.Owner;
        var viewer = world.Viewer;

        var created = await CreateProductAsync(owner.Client, "Owner's product", UniqueSku("own"), 9.99m, 1);
        var updated = await UpdateProductOkAsync(owner.Client, created, "Owner's product v2");
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteProductAsync(owner.Client, updated.Id)).StatusCode);
        Assert.Equal(DemoData.AcmeProductCount, (await DemoApi.AllProductsAsync(owner.Client)).Count);

        var newcomer = UniqueEmail("hired");
        var createUser = await CreateUserAsync(owner.Client, newcomer);
        Assert.Equal(HttpStatusCode.Created, createUser.StatusCode);
        Assert.Equal(DemoData.AcmeUserCount + 1, (await ListUsersAsync(owner.Client)).Count);

        // Granting takes effect on the viewer's very next request.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostProductAsync(viewer.Client, "Before grant", UniqueSku("g"))).StatusCode);
        await GrantAsync(owner.Client, viewer.Auth.UserId, Permission.ProductsManage);
        Assert.Equal(HttpStatusCode.Created, (await PostProductAsync(viewer.Client, "After grant", UniqueSku("g"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await RevokeAsync(owner.Client, viewer.Auth.UserId, Permission.ProductsManage)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostProductAsync(viewer.Client, "After revoke", UniqueSku("g"))).StatusCode);
    }

    [Fact]
    public async Task GlobexOwner_CanDoEverythingInItsOwnTenant()
    {
        var world = await FreshWorldAsync();
        var globex = world.Globex;

        var created = await CreateProductAsync(globex.Client, "Globex product", UniqueSku("glx"), 3m, 4);
        var updated = await UpdateProductOkAsync(globex.Client, created, "Globex product v2");
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteProductAsync(globex.Client, updated.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateUserAsync(globex.Client, UniqueEmail("globex-hire"))).StatusCode);
        Assert.Equal(2, (await ListUsersAsync(globex.Client)).Count);
        Assert.Equal(DemoData.GlobexProductCount, (await DemoApi.AllProductsAsync(globex.Client)).Count);
    }

    [Fact]
    public async Task ANewSku_CanBeUsedOnceInEachTenant()
    {
        var world = await FreshWorldAsync();
        var owner = world.Owner;
        var globex = world.Globex;
        var sku = UniqueSku("both");

        Assert.Equal(HttpStatusCode.Created, (await PostProductAsync(owner.Client, "Acme's", sku)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostProductAsync(globex.Client, "Globex's", sku)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostProductAsync(owner.Client, "Acme's again", sku)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostProductAsync(globex.Client, "Globex's again", sku)).StatusCode);
    }

    // ---- Several demo users at once ---------------------------------------------------------------------------------------

    [Fact]
    public async Task DemoUsersWorkingAtOnce_NeverSeeOrDisturbEachOthersTenant()
    {
        var world = await FreshWorldAsync();
        var owner = world.Owner;
        var manager = world.Manager;
        var viewer = world.Viewer;
        var globex = world.Globex;
        const int creatorsPerTenantUser = 8;

        var writes = Enumerable.Range(0, creatorsPerTenantUser).SelectMany(i => new Func<Task<HttpResponseMessage>>[]
        {
            () => PostProductAsync(owner.Client, $"Owner batch {i}", UniqueSku("o")),
            () => PostProductAsync(manager.Client, $"Manager batch {i}", UniqueSku("m")),
            () => PostProductAsync(globex.Client, $"Globex batch {i}", UniqueSku("g")),
        });
        var reads = Enumerable.Range(0, creatorsPerTenantUser).Select<int, Func<Task<HttpResponseMessage>>>(_ => () => ListProductsAsync(viewer.Client, "pageSize=100"));

        var statuses = await RunInParallelAsync(writes.Concat(reads));

        Assert.All(statuses.Take(creatorsPerTenantUser * 3), status => Assert.Equal(HttpStatusCode.Created, status));
        Assert.All(statuses.Skip(creatorsPerTenantUser * 3), status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(DemoData.AcmeProductCount + 2 * creatorsPerTenantUser, (await DemoApi.AllProductsAsync(viewer.Client)).Count);
        var globexProducts = await DemoApi.AllProductsAsync(globex.Client);
        Assert.Equal(DemoData.GlobexProductCount + creatorsPerTenantUser, globexProducts.Count);
        Assert.DoesNotContain(globexProducts, p => p.Name.StartsWith("Owner batch") || p.Name.StartsWith("Manager batch"));
    }

    [Fact]
    public async Task ThreeDemoUsersRacingForOneNewSku_AcmeGetsExactlyOneWinnerAndGlobexItsOwn()
    {
        var world = await FreshWorldAsync();
        var owner = world.Owner;
        var manager = world.Manager;
        var globex = world.Globex;
        var sku = UniqueSku("race");

        var statuses = await RunInParallelAsync(new Func<Task<HttpResponseMessage>>[]
        {
            () => PostProductAsync(owner.Client, "Acme owner's", sku),
            () => PostProductAsync(manager.Client, "Acme manager's", sku),
            () => PostProductAsync(globex.Client, "Globex's", sku),
        });

        Assert.Equal(new HttpStatusCode?[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, statuses.Take(2).Order());
        Assert.Equal(HttpStatusCode.Created, statuses[2]);
        Assert.Equal(2, (await DemoDbReader.ProductsAsync(Db)).Count(p => p.Sku == sku));
    }
}
