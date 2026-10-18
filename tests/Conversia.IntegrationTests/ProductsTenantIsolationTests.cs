using System.Net;
using System.Net.Http.Json;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Products live in the tenant of the caller's JWT and nowhere else: another tenant's products are invisible
/// (list, search, get) and untouchable (update, delete), and the SKU is unique per tenant, not globally.
/// Each cross-tenant refusal is checked against the owning tenant's own successful access to the same id.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ProductsTenantIsolationTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProductsTenantIsolationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<ProductTenant> NewTenantAsync(string prefix) => StartProductTenantAsync(_factory, _client, prefix);

    [Fact]
    public async Task TheSameSkuCanExistInTwoTenants_EachSeeingOnlyItsOwn()
    {
        using var a = await NewTenantAsync("a");
        using var b = await NewTenantAsync("b");
        var sku = UniqueSku("shared");

        var inA = await CreateProductAsync(a.Owner.Client, "From A", sku, 1m, 1);
        var inB = await CreateProductAsync(b.Owner.Client, "From B", sku, 2m, 2);

        Assert.NotEqual(inA.Id, inB.Id);
        Assert.Equal("From A", Assert.Single((await ListProductsOkAsync(a.Owner.Client)).Items).Name);
        Assert.Equal("From B", Assert.Single((await ListProductsOkAsync(b.Owner.Client)).Items).Name);
        var rows = await FindProductsBySkuAsync(_factory, sku);
        Assert.Equal(new[] { a.TenantId, b.TenantId }.Order(), rows.Select(r => r.TenantId).Order());

        // Within each tenant the SKU is still taken.
        Assert.Equal(HttpStatusCode.Conflict, (await PostProductAsync(a.Owner.Client, "Again", sku)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostProductAsync(b.Owner.Client, "Again", sku)).StatusCode);
    }

    [Fact]
    public async Task ASkuHeldByAnotherTenant_DoesNotMakeCreationFailOrRevealItself()
    {
        using var a = await NewTenantAsync("a");
        using var b = await NewTenantAsync("b");
        var sku = UniqueSku("secret");
        await CreateProductAsync(b.Owner.Client, "B's", sku);

        var response = await PostProductAsync(a.Owner.Client, "A's", sku);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(a.TenantId, Assert.Single(await FindProductsBySkuAsync(_factory, sku), r => r.Name == "A's").TenantId);
    }

    [Fact]
    public async Task ListAndSearch_NeverShowAnotherTenantsProducts()
    {
        using var a = await NewTenantAsync("a");
        using var b = await NewTenantAsync("b");
        var onlyInB = $"Zanzibar-{Guid.NewGuid():N}";
        await CreateProductAsync(a.Owner.Client, "A's own");
        await CreateProductAsync(b.Owner.Client, onlyInB);

        var bSearch = await ListProductsOkAsync(b.Owner.Client, $"search={onlyInB}");
        var aSearch = await ListProductsOkAsync(a.Owner.Client, $"search={onlyInB}");
        var aAll = await ListProductsOkAsync(a.Owner.Client, "pageSize=100");

        Assert.Equal(1, bSearch.TotalCount);
        Assert.Equal(0, aSearch.TotalCount);
        Assert.Empty(aSearch.Items);
        Assert.Equal(1, aAll.TotalCount);
        Assert.DoesNotContain(aAll.Items, p => p.Name == onlyInB);
    }

    [Fact]
    public async Task GetUpdateDelete_OfAnotherTenantsProduct_Return404IndistinguishableFromAnUnknownId_AndChangeNothing()
    {
        using var a = await NewTenantAsync("a");
        using var b = await NewTenantAsync("b");
        var inA = await CreateProductAsync(a.Owner.Client, "A's product", UniqueSku(), 5m, 5);
        var unknownId = Guid.NewGuid();

        // The owning tenant reaches it with every verb ...
        Assert.Equal(HttpStatusCode.OK, (await GetProductAsync(a.Owner.Client, inA.Id)).StatusCode);
        var ownUpdate = await UpdateProductOkAsync(a.Owner.Client, inA, "A's product", 5m, 5);
        Assert.Equal(inA.Id, ownUpdate.Id);

        // ... while the other tenant gets the same answer as for an id that never existed.
        var crossGet = await GetProductAsync(b.Owner.Client, inA.Id);
        var crossPut = await PutProductAsync(b.Owner.Client, inA.Id, "Hijacked", 0m, 0, ownUpdate.RowVersion);
        var crossDelete = await DeleteProductAsync(b.Owner.Client, inA.Id);
        var unknownGet = await GetProductAsync(b.Owner.Client, unknownId);
        var unknownPut = await PutProductAsync(b.Owner.Client, unknownId, "Hijacked", 0m, 0, ownUpdate.RowVersion);
        var unknownDelete = await DeleteProductAsync(b.Owner.Client, unknownId);

        foreach (var (cross, unknown) in new[] { (crossGet, unknownGet), (crossPut, unknownPut), (crossDelete, unknownDelete) })
        {
            Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
            Assert.Equal(unknown.StatusCode, cross.StatusCode);
            var crossProblem = await ReadProblemJsonAsync(cross);
            var unknownProblem = await ReadProblemJsonAsync(unknown);
            Assert.Equal(unknownProblem.Title, crossProblem.Title);
            Assert.Equal(unknownProblem.Detail, crossProblem.Detail);
            Assert.Equal(unknownProblem.Status, crossProblem.Status);
            var body = await cross.Content.ReadAsStringAsync();
            Assert.DoesNotContain(a.TenantId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(inA.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
        }

        await AssertProductUnchangedAsync(a.Owner.Client, ownUpdate);
    }

    [Fact]
    public async Task ACrossTenantWriteWithTheCorrectRowVersionStillCannotTouchTheProduct()
    {
        using var a = await NewTenantAsync("a");
        using var b = await NewTenantAsync("b");
        using var managerB = await b.AddMemberAsync("managerB", Permission.ProductsManage, Permission.ProductsView);
        var inA = await CreateProductAsync(a.Owner.Client);

        var viaOwner = await PutProductAsync(b.Owner.Client, inA.Id, "Hijacked", 0m, 0, inA.RowVersion);
        var viaManager = await DeleteProductAsync(managerB.Client, inA.Id);

        Assert.Equal(HttpStatusCode.NotFound, viaOwner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viaManager.StatusCode);
        await AssertProductUnchangedAsync(a.Owner.Client, inA);
        Assert.Equal(a.TenantId, (await FindProductByIdAsync(_factory, inA.Id))!.TenantId);
    }

    [Fact]
    public async Task DeletingInOneTenant_LeavesTheOtherTenantsProductWithTheSameSkuAlone()
    {
        using var a = await NewTenantAsync("a");
        using var b = await NewTenantAsync("b");
        var sku = UniqueSku("twin");
        var inA = await CreateProductAsync(a.Owner.Client, sku: sku);
        var inB = await CreateProductAsync(b.Owner.Client, sku: sku);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteProductAsync(a.Owner.Client, inA.Id)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await GetProductAsync(a.Owner.Client, inA.Id)).StatusCode);
        await AssertProductUnchangedAsync(b.Owner.Client, inB);
        Assert.Single(await FindProductsBySkuAsync(_factory, sku));
    }

    [Fact]
    public async Task Create_IgnoresATenantIdIdOrAuditFieldsInTheBody_AndUsesTheTenantOfTheToken()
    {
        using var a = await NewTenantAsync("a");
        using var b = await NewTenantAsync("b");
        var sku = UniqueSku("forge");
        var forgedId = Guid.NewGuid();

        var response = await b.Owner.Client.PostAsJsonAsync(ProductsUrl, new
        {
            name = "Forged",
            sku,
            price = 1m,
            stock = 1,
            tenantId = a.TenantId,
            id = forgedId,
            createdBy = a.Owner.UserId,
            rowVersion = Convert.ToBase64String(new byte[8]),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);
        Assert.NotEqual(forgedId, created.Id);
        Assert.Equal(b.Owner.UserId, created.CreatedBy);
        var row = Assert.Single(await FindProductsBySkuAsync(_factory, sku));
        Assert.Equal(b.TenantId, row.TenantId);
        Assert.Equal(0, (await ListProductsOkAsync(a.Owner.Client)).TotalCount);
        Assert.Equal(1, (await ListProductsOkAsync(b.Owner.Client)).TotalCount);
    }

    [Fact]
    public async Task AMembersProducts_BelongToTheMembersTenant()
    {
        using var a = await NewTenantAsync("a");
        using var b = await NewTenantAsync("b");
        using var managerA = await a.AddMemberAsync("managerA", Permission.ProductsManage);

        var created = await CreateProductAsync(managerA.Client);

        Assert.Equal(a.TenantId, (await FindProductByIdAsync(_factory, created.Id))!.TenantId);
        AssertSameProduct(created, await GetProductOkAsync(a.Owner.Client, created.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await GetProductAsync(b.Owner.Client, created.Id)).StatusCode);
    }
}
