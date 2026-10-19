using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Conversia.Application.Exceptions;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// End-to-end tests of <c>/api/products</c> (create, read, update, delete and their error contract) against the
/// real Testcontainers PostgreSQL. Authorization, tenant isolation, listing and concurrency have their own classes.
/// Every negative assertion is paired, in the same test, with a positive one proving the route is live.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ProductsEndpointTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProductsEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<TestTenant> NewTenantAsync(string prefix = "shop") => StartTenantAsync(_factory, _client, prefix);

    // ---- Lifecycle -------------------------------------------------------------------------

    [Fact]
    public async Task Product_CanBeCreatedReadUpdatedListedAndDeleted()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var sku = UniqueSku();

        var created = await CreateProductAsync(http, "Widget Pro", sku, 12.34m, 5);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Widget Pro", created.Name);
        Assert.Equal(sku, created.Sku);
        Assert.Equal(12.34m, created.Price);
        Assert.Equal(5, created.Stock);
        Assert.False(string.IsNullOrEmpty(created.RowVersion));

        AssertSameProduct(created, await GetProductOkAsync(http, created.Id));
        var listed = await ListProductsOkAsync(http);
        Assert.Equal(1, listed.TotalCount);
        AssertSameProduct(created, Assert.Single(listed.Items));

        var updated = await UpdateProductOkAsync(http, created, "Widget Max", 99.99m, 0);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Widget Max", updated.Name);
        Assert.Equal(99.99m, updated.Price);
        Assert.Equal(0, updated.Stock);
        Assert.Equal(sku, updated.Sku);
        Assert.NotEqual(created.RowVersion, updated.RowVersion);
        AssertSameProduct(updated, await GetProductOkAsync(http, created.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteProductAsync(http, created.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetProductAsync(http, created.Id)).StatusCode);
        Assert.Equal(0, (await ListProductsOkAsync(http)).TotalCount);
        Assert.Empty(await FindProductsBySkuAsync(_factory, sku));
    }

    [Fact]
    public async Task ResponseJson_UsesTheDocumentedPropertyNames()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);

        var single = JsonDocument.Parse(await (await GetProductAsync(http, created.Id)).Content.ReadAsStringAsync()).RootElement;
        var list = JsonDocument.Parse(await (await ListProductsAsync(http)).Content.ReadAsStringAsync()).RootElement;

        var expectedProduct = new[] { "createdAt", "createdBy", "id", "name", "price", "rowVersion", "sku", "stock", "updatedAt", "updatedBy" };
        Assert.Equal(expectedProduct, single.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "items", "page", "pageSize", "totalCount" }, list.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(JsonValueKind.Number, single.GetProperty("price").ValueKind);
        Assert.Equal(JsonValueKind.String, single.GetProperty("rowVersion").ValueKind);
        _ = Convert.FromBase64String(single.GetProperty("rowVersion").GetString()!); // base64 or this throws
        Assert.Equal(JsonValueKind.Null, single.GetProperty("updatedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, single.GetProperty("updatedBy").ValueKind);
        Assert.Equal(created.Id, single.GetProperty("id").GetGuid());
    }

    // ---- Create ----------------------------------------------------------------------------

    [Fact]
    public async Task Create_Returns201WithALocationThatPointsAtTheNewProduct()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;

        var response = await PostProductAsync(http, "Located", UniqueSku(), 3m, 2);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadProductAsync(response);
        Assert.NotNull(response.Headers.Location);
        var location = new Uri(_client.BaseAddress!, response.Headers.Location);
        Assert.Equal($"/api/products/{body.Id}", location.AbsolutePath, ignoreCase: true);

        var followed = await http.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
        AssertSameProduct(body, await ReadProductAsync(followed));
    }

    [Fact]
    public async Task Create_StampsTheAuditFieldsCentrallyWithTheActingUser()
    {
        using var tenant = await NewTenantAsync();
        using var manager = await tenant.AddMemberAsync("manager", Permission.ProductsManage);
        var before = DateTime.UtcNow.AddMinutes(-1);

        var byOwner = await CreateProductAsync(tenant.Owner.Client);
        var byManager = await CreateProductAsync(manager.Client);

        var after = DateTime.UtcNow.AddMinutes(1);
        Assert.Equal(tenant.Owner.UserId, byOwner.CreatedBy);
        Assert.Equal(manager.UserId, byManager.CreatedBy);
        Assert.InRange(byOwner.CreatedAt, before, after);
        Assert.InRange(byManager.CreatedAt, before, after);
        Assert.Null(byOwner.UpdatedAt);
        Assert.Null(byOwner.UpdatedBy);

        var row = await FindProductByIdAsync(_factory, byManager.Id);
        Assert.NotNull(row);
        Assert.Equal(manager.UserId, row!.CreatedBy);
        Assert.Equal(tenant.TenantId, row.TenantId);
    }

    [Fact]
    public async Task Create_StoresTheSkuTrimmed()
    {
        using var tenant = await NewTenantAsync();
        var sku = UniqueSku("trim");

        var created = await CreateProductAsync(tenant.Owner.Client, sku: $"  {sku}\t");

        Assert.Equal(sku.Length, created.Sku.Length);
        Assert.Equal(sku, created.Sku, ignoreCase: true);
        var row = Assert.Single(await FindProductsBySkuAsync(_factory, sku));
        Assert.Equal(sku.Length, row.Sku.Length);
    }

    [Fact]
    public async Task Create_WithAPaddedSkuOfExactlyTheMaximumLength_Returns201AndStoresItTrimmed()
    {
        using var tenant = await NewTenantAsync();
        var sku = new string('P', 50);

        var response = await PostProductAsync(tenant.Owner.Client, "Padded at the limit", $"  {sku}\t");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode); // the 50-character limit applies to the trimmed value
        var created = await ReadProductAsync(response);
        Assert.Equal(50, created.Sku.Length);
        Assert.Equal(sku, created.Sku);
        var row = Assert.Single(await FindProductsBySkuAsync(_factory, sku));
        Assert.Equal(50, row.Sku.Length);
    }

    [Fact]
    public async Task Create_WithAPaddedSkuOneOverTheMaximumLength_Returns400UnderSkuAndStoresNothing()
    {
        using var tenant = await NewTenantAsync();
        var sku = new string('P', 51);

        var response = await PostProductAsync(tenant.Owner.Client, "Padded over the limit", $"  {sku}\t");

        await AssertValidationErrorUnderAsync(response, "Sku");
        Assert.Empty(await FindProductsBySkuAsync(_factory, sku));
        Assert.Empty((await ListProductsOkAsync(tenant.Owner.Client)).Items);
    }

    [Fact]
    public async Task Create_WithAPaddedNameOfExactlyTheMaximumLength_Returns201AndStoresItTrimmed()
    {
        using var tenant = await NewTenantAsync();
        var name = new string('N', 200);
        var sku = UniqueSku("pname");

        var response = await PostProductAsync(tenant.Owner.Client, $"  {name}\t", sku);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode); // the 200-character limit applies to the trimmed value
        var created = await ReadProductAsync(response);
        Assert.Equal(200, created.Name.Length);
        Assert.Equal(name, created.Name);
        var row = Assert.Single(await FindProductsBySkuAsync(_factory, sku));
        Assert.Equal(200, row.Name.Length);
        Assert.Equal(name, row.Name);
    }

    [Fact]
    public async Task Create_WithAPaddedNameOneOverTheMaximumLength_Returns400UnderNameAndStoresNothing()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var sku = UniqueSku("pname");
        await CreateProductAsync(http); // the caller can create products, so the route is live and the 400 below is a real rejection

        var response = await PostProductAsync(http, $"  {new string('N', 201)}\t", sku);

        await AssertValidationErrorUnderAsync(response, "Name");
        Assert.Empty(await FindProductsBySkuAsync(_factory, sku));
        Assert.Single((await ListProductsOkAsync(http)).Items);
    }

    public static TheoryData<int, int, string, int> AcceptedBoundaryValues => new()
    {
        { 1, 1, "0", 0 },                           // shortest name, shortest SKU, price 0, stock 0
        { 200, 50, "999999999.99", int.MaxValue },  // longest name, longest SKU, highest price, highest stock
        { 10, 10, "0.01", 1 },
    };

    [Theory]
    [MemberData(nameof(AcceptedBoundaryValues))]
    public async Task Create_AtTheBoundariesOfEveryRule_IsAcceptedAndRoundTripsExactly(
        int nameLength, int skuLength, string priceText, int stock)
    {
        using var tenant = await NewTenantAsync(); // a fresh tenant, so the fixed-looking SKU cannot collide
        var http = tenant.Owner.Client;
        var name = new string('n', nameLength);
        var sku = new string('S', skuLength);
        var price = decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);

        var created = await CreateProductAsync(http, name, sku, price, stock);

        var read = await GetProductOkAsync(http, created.Id);
        Assert.Equal(name, read.Name);
        Assert.Equal(sku, read.Sku);
        Assert.Equal(price, read.Price);
        Assert.Equal(stock, read.Stock);
    }

    public static TheoryData<string?, string?, decimal, int, string> RejectedCreateInputs
    {
        get
        {
            var data = new TheoryData<string?, string?, decimal, int, string>();
            foreach (var name in new string?[] { null, "", "   ", new string('n', 201) })
            {
                data.Add(name, "OK-SKU", 1m, 1, "Name");
            }

            foreach (var sku in new string?[] { null, "", "  ", new string('S', 51), "has space", "a.b", "a/b", "a%b", "a'b", "a;b", "a*b", "😀" })
            {
                data.Add("Name", sku, 1m, 1, "Sku");
            }

            foreach (var price in new[] { -0.01m, -1m, 1000000000m, 999999999.991m, 0.001m, 1.005m })
            {
                data.Add("Name", "OK-SKU", price, 1, "Price");
            }

            data.Add("Name", "OK-SKU", 1m, -1, "Stock");
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RejectedCreateInputs))]
    public async Task Create_WithInvalidInput_Returns400UnderTheOffendingFieldAndWritesNothing(
        string? name, string? sku, decimal price, int stock, string field)
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        await CreateProductAsync(http); // the route is live and accepts valid input for this very caller

        var response = await PostProductAsync(http, name, sku, price, stock);

        await AssertValidationErrorUnderAsync(response, field);
        Assert.Equal(1, (await ListProductsOkAsync(http)).TotalCount);
        if (!string.IsNullOrWhiteSpace(sku))
        {
            Assert.Empty(await FindProductsBySkuAsync(_factory, sku));
        }
    }

    [Fact]
    public async Task Create_WithEveryFieldInvalid_ReportsEachFieldAtOnce()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        await CreateProductAsync(http);

        var response = await PostProductAsync(http, "", "not valid!", -1m, -1);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>();
        var keys = problem!.Errors.Keys.Select(k => k.ToLowerInvariant()).ToHashSet();
        Assert.Superset(new HashSet<string> { "name", "sku", "price", "stock" }, keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ this is not valid json ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"name\":\"x\",\"sku\":\"x-1\",\"price\":\"not a number\",\"stock\":1}")]
    [InlineData("{\"name\":\"x\",\"sku\":\"x-1\",\"price\":1,\"stock\":99999999999}")]
    [InlineData("{\"name\":\"x\",\"sku\":\"x-1\",\"price\":1,\"stock\":1.5}")]
    [InlineData("{\"name\":\"x\",\"sku\":\"x-1\",\"price\":1e400,\"stock\":1}")]
    [InlineData("{\"name\":[1,2],\"sku\":{},\"price\":1,\"stock\":1}")]
    public async Task Create_WithUnusableBody_Returns400ProblemJsonNeverAnUnhandledException(string body)
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        await CreateProductAsync(http);

        var response = await http.PostAsync(ProductsUrl, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertNoInternalsLeaked(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, (await ListProductsOkAsync(http)).TotalCount);
    }

    [Fact]
    public async Task Create_WithWrongContentType_Returns400Or415NeverAnUnhandledException()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        await CreateProductAsync(http);

        var response = await http.PostAsync(ProductsUrl, new StringContent("name=x&sku=y", Encoding.UTF8, "text/plain"));

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType,
            $"Expected 400 or 415, got {(int)response.StatusCode}.");
        Assert.Equal(1, (await ListProductsOkAsync(http)).TotalCount);
    }

    // ---- Duplicate SKU ---------------------------------------------------------------------

    public static TheoryData<string> DuplicateSkuSpellings => new()
    {
        "same", "upper", "lower", "padded", "upper-padded",
    };

    private static string Spell(string sku, string spelling) => spelling switch
    {
        "same" => sku,
        "upper" => sku.ToUpperInvariant(),
        "lower" => sku.ToLowerInvariant(),
        "padded" => $"  {sku}  ",
        _ => $"\t{sku.ToUpperInvariant()}\n",
    };

    [Theory]
    [MemberData(nameof(DuplicateSkuSpellings))]
    public async Task Create_WithASkuTheTenantAlreadyHolds_Returns409ProblemJsonAndKeepsTheOriginal(string spelling)
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var sku = UniqueSku("Dup");
        var original = await CreateProductAsync(http, "Original", sku, 5m, 5);

        var response = await PostProductAsync(http, "Impostor", Spell(sku, spelling), 9m, 9);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadProblemJsonAsync(response);
        Assert.Equal(new DuplicateSkuException().Message, problem.Detail);
        AssertNoInternalsLeaked(await response.Content.ReadAsStringAsync());
        await AssertProductUnchangedAsync(http, original);
        Assert.Equal(1, (await ListProductsOkAsync(http)).TotalCount);
        Assert.Single(await FindProductsBySkuAsync(_factory, sku));
    }

    [Fact]
    public async Task Create_WithASkuOfAProductThatWasDeleted_Succeeds()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var sku = UniqueSku("Reuse");
        var first = await CreateProductAsync(http, sku: sku);
        Assert.Equal(HttpStatusCode.Conflict, (await PostProductAsync(http, "Again", sku)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteProductAsync(http, first.Id)).StatusCode);

        var second = await CreateProductAsync(http, sku: sku);
        Assert.NotEqual(first.Id, second.Id);
    }

    // ---- Get -------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WithAnUnknownId_Returns404ProblemJsonWhileTheRouteServesRealIds()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);
        Assert.Equal(HttpStatusCode.OK, (await GetProductAsync(http, created.Id)).StatusCode);

        foreach (var unknown in new[] { Guid.NewGuid(), Guid.Empty })
        {
            var response = await GetProductAsync(http, unknown);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await ReadProblemJsonAsync(response);
            AssertNoInternalsLeaked(await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task EveryVerbWithANonGuidId_Returns404WhileTheSameVerbWorksForARealId()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var product = await CreateProductAsync(http);
        var body = new { name = "Renamed", price = 1m, stock = 1, rowVersion = product.RowVersion };

        Assert.Equal(HttpStatusCode.OK, (await GetProductAsync(http, product.Id)).StatusCode);
        var putOk = await http.PutAsJsonAsync($"{ProductsUrl}/{product.Id}", body);
        Assert.Equal(HttpStatusCode.OK, putOk.StatusCode);

        foreach (var id in new[] { "not-a-guid", "12345", $"{product.Id}x" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"{ProductsUrl}/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.PutAsJsonAsync($"{ProductsUrl}/{id}", body)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"{ProductsUrl}/{id}")).StatusCode);
        }

        Assert.Equal("Renamed", (await GetProductOkAsync(http, product.Id)).Name); // still there, still the PUT's value
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteProductAsync(http, product.Id)).StatusCode);
    }

    // ---- Update ----------------------------------------------------------------------------

    [Fact]
    public async Task Update_ReturnsTheNewStateWithANewRowVersionAndAuditFields()
    {
        using var tenant = await NewTenantAsync();
        using var editor = await tenant.AddMemberAsync("editor", Permission.ProductsManage);
        var created = await CreateProductAsync(tenant.Owner.Client);
        var before = DateTime.UtcNow.AddMinutes(-1);

        var updated = await UpdateProductOkAsync(editor.Client, created, "Edited", 77.7m, 11);

        Assert.NotEqual(created.RowVersion, updated.RowVersion);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.Equal(tenant.Owner.UserId, updated.CreatedBy);
        Assert.Equal(editor.UserId, updated.UpdatedBy);
        Assert.NotNull(updated.UpdatedAt);
        Assert.True(updated.UpdatedAt >= updated.CreatedAt, "UpdatedAt must not precede CreatedAt.");
        Assert.InRange(updated.UpdatedAt!.Value, before, DateTime.UtcNow.AddMinutes(1));
        AssertSameProduct(updated, await GetProductOkAsync(tenant.Owner.Client, created.Id));
    }

    [Fact]
    public async Task Update_EachSuccessfulWriteIssuesANewRowVersion()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var v1 = await CreateProductAsync(http);

        var v2 = await UpdateProductOkAsync(http, v1);
        var v3 = await UpdateProductOkAsync(http, v2);

        Assert.Equal(3, new[] { v1.RowVersion, v2.RowVersion, v3.RowVersion }.Distinct().Count());
    }

    [Fact]
    public async Task Update_IgnoresASkuTenantOrIdInTheBody()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);

        var response = await http.PutAsJsonAsync($"{ProductsUrl}/{created.Id}", new
        {
            name = "Renamed",
            price = 2m,
            stock = 2,
            rowVersion = created.RowVersion,
            sku = "HACKED-SKU",
            id = Guid.NewGuid(),
            tenantId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadProductAsync(response);
        Assert.Equal(created.Sku, updated.Sku);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Renamed", updated.Name);
        Assert.Empty(await FindProductsBySkuAsync(_factory, "HACKED-SKU"));
        Assert.Equal(tenant.TenantId, (await FindProductByIdAsync(_factory, created.Id))!.TenantId);
    }

    public static TheoryData<string?, decimal, int, string> RejectedUpdateInputs => new()
    {
        { "", 1m, 1, "Name" },
        { "   ", 1m, 1, "Name" },
        { null, 1m, 1, "Name" },
        { new string('n', 201), 1m, 1, "Name" },
        { "Name", -0.01m, 1, "Price" },
        { "Name", 1000000000m, 1, "Price" },
        { "Name", 999999999.991m, 1, "Price" },
        { "Name", 1.005m, 1, "Price" },
        { "Name", 1m, -1, "Stock" },
    };

    [Theory]
    [MemberData(nameof(RejectedUpdateInputs))]
    public async Task Update_WithInvalidInput_Returns400AndLeavesTheProductAndItsRowVersionUntouched(
        string? name, decimal price, int stock, string field)
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);

        var response = await PutProductAsync(http, created.Id, name, price, stock, created.RowVersion);

        await AssertValidationErrorUnderAsync(response, field);
        await AssertProductUnchangedAsync(http, created);
        // The rejected attempt did not consume the row version: the same version still updates.
        Assert.Equal(HttpStatusCode.OK, (await PutProductAsync(http, created.Id, "Valid", 1m, 1, created.RowVersion)).StatusCode);
    }

    [Fact]
    public async Task Update_WithAPaddedNameOfExactlyTheMaximumLength_Returns200AndStoresItTrimmed()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);
        var name = new string('N', 200);

        var response = await PutProductAsync(http, created.Id, $"  {name}\t", 5m, 5, created.RowVersion);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the 200-character limit applies to the trimmed value
        var updated = await ReadProductAsync(response);
        Assert.Equal(200, updated.Name.Length);
        Assert.Equal(name, updated.Name);
        AssertSameProduct(updated, await GetProductOkAsync(http, created.Id));
        Assert.Equal(name, (await FindProductByIdAsync(_factory, created.Id))!.Name);
    }

    [Fact]
    public async Task Update_WithAPaddedNameOneOverTheMaximumLength_Returns400UnderNameAndLeavesTheProductUntouched()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);

        var response = await PutProductAsync(http, created.Id, $"  {new string('N', 201)}\t", 5m, 5, created.RowVersion);

        await AssertValidationErrorUnderAsync(response, "Name");
        await AssertProductUnchangedAsync(http, created);
        // The rejected attempt did not consume the row version: the same version still updates.
        Assert.Equal(HttpStatusCode.OK, (await PutProductAsync(http, created.Id, "Valid", 1m, 1, created.RowVersion)).StatusCode);
    }

    public static TheoryData<string?> MissingOrMalformedRowVersions => new()
    {
        null, "", "   ", "not base64!", "abc", "====", "AAAA AAAA",
    };

    [Theory]
    [MemberData(nameof(MissingOrMalformedRowVersions))]
    public async Task Update_WithAMissingOrNonBase64RowVersion_Returns400UnderRowVersionAndChangesNothing(string? rowVersion)
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);

        var response = await PutProductAsync(http, created.Id, "Changed", 2m, 2, rowVersion);

        await AssertValidationErrorUnderAsync(response, "RowVersion");
        await AssertProductUnchangedAsync(http, created);
        Assert.Equal(HttpStatusCode.OK, (await PutProductAsync(http, created.Id, "Changed", 2m, 2, created.RowVersion)).StatusCode);
    }

    [Fact]
    public async Task Update_WithoutARowVersionPropertyAtAll_Returns400UnderRowVersion()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);

        var response = await http.PutAsJsonAsync($"{ProductsUrl}/{created.Id}", new { name = "Changed", price = 2m, stock = 2 });

        await AssertValidationErrorUnderAsync(response, "RowVersion");
        await AssertProductUnchangedAsync(http, created);
    }

    [Fact]
    public async Task Update_WithAStaleRowVersion_Returns409ProblemJsonAndKeepsTheFirstWritersValues()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var stale = await CreateProductAsync(http);
        var winner = await UpdateProductOkAsync(http, stale, "Winner", 20m, 20);

        var loser = await PutProductAsync(http, stale.Id, "Loser", 30m, 30, stale.RowVersion);

        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        var problem = await ReadProblemJsonAsync(loser);
        Assert.Equal(new ProductConcurrencyException().Message, problem.Detail);
        AssertNoInternalsLeaked(await loser.Content.ReadAsStringAsync());
        await AssertProductUnchangedAsync(http, winner);

        // Reloading and retrying with the current version succeeds.
        var retried = await UpdateProductOkAsync(http, winner, "Loser, retried", 30m, 30);
        Assert.Equal("Loser, retried", retried.Name);
    }

    [Fact]
    public async Task Update_WithWellFormedBase64ThatIsNotTheCurrentVersion_Returns409AndChangesNothing()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);
        var zeros = Convert.ToBase64String(new byte[8]);
        Assert.NotEqual(zeros, created.RowVersion);

        var response = await PutProductAsync(http, created.Id, "Changed", 2m, 2, zeros);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await ReadProblemJsonAsync(response);
        await AssertProductUnchangedAsync(http, created);
        Assert.Equal(HttpStatusCode.OK, (await PutProductAsync(http, created.Id, "Changed", 2m, 2, created.RowVersion)).StatusCode);
    }

    [Fact]
    public async Task Update_WithABase64RowVersionOfTheWrongLength_IsRefusedInAControlledWayAndChangesNothing()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);

        var response = await PutProductAsync(http, created.Id, "Changed", 2m, 2, Convert.ToBase64String(new byte[] { 1, 2, 3 }));

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"Expected 400 or 409, got {(int)response.StatusCode}.");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await AssertProductUnchangedAsync(http, created);
    }

    [Fact]
    public async Task Update_OfAnUnknownId_Returns404ProblemJsonWhileARealIdUpdates()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);
        await UpdateProductOkAsync(http, created);

        var response = await PutProductAsync(http, Guid.NewGuid(), "Ghost", 1m, 1, created.RowVersion);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await ReadProblemJsonAsync(response);
    }

    [Fact]
    public async Task Update_AfterTheProductWasDeleted_Returns404AndDoesNotResurrectIt()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteProductAsync(http, created.Id)).StatusCode);

        var response = await PutProductAsync(http, created.Id, "Zombie", 1m, 1, created.RowVersion);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetProductAsync(http, created.Id)).StatusCode);
        Assert.Equal(0, (await ListProductsOkAsync(http)).TotalCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"name\":\"x\",\"price\":\"abc\",\"stock\":1,\"rowVersion\":\"AAAAAAAAAAA=\"}")]
    [InlineData("{\"name\":\"x\",\"price\":1,\"stock\":1,\"rowVersion\":12345}")]
    public async Task Update_WithUnusableBody_Returns400ProblemJsonAndChangesNothing(string body)
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var created = await CreateProductAsync(http);

        var response = await http.PutAsync($"{ProductsUrl}/{created.Id}", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertNoInternalsLeaked(await response.Content.ReadAsStringAsync());
        await AssertProductUnchangedAsync(http, created);
    }

    // ---- Delete ----------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesOnlyThatProduct_AndDeletingTwiceIsA404()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var doomed = await CreateProductAsync(http);
        var survivor = await CreateProductAsync(http);

        var first = await DeleteProductAsync(http, doomed.Id);
        var second = await DeleteProductAsync(http, doomed.Id);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        await ReadProblemJsonAsync(second);
        await AssertProductUnchangedAsync(http, survivor);
        Assert.Equal(1, (await ListProductsOkAsync(http)).TotalCount);
    }

    [Fact]
    public async Task Delete_OfAnUnknownId_Returns404ProblemJsonWhileARealIdIsDeleted()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        var real = await CreateProductAsync(http);

        var unknown = await DeleteProductAsync(http, Guid.NewGuid());
        var known = await DeleteProductAsync(http, real.Id);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await ReadProblemJsonAsync(unknown);
        Assert.Equal(HttpStatusCode.NoContent, known.StatusCode);
    }
}
