using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Conversia.Application.DTOs.Products;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// <c>GET /api/products</c>: paging bounds and metadata, ordering by name then SKU, the literal
/// (never wildcard) case-insensitive search, and the database round-trip budget.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ProductsListTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProductsListTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<TestTenant> NewTenantAsync() => StartTenantAsync(_factory, _client, "list");

    /// <summary>Creates P01..P{count} (zero-padded so name order is numeric order), created in reverse order.</summary>
    private static async Task<List<string>> SeedNumberedAsync(HttpClient http, int count)
    {
        var names = Enumerable.Range(1, count).Select(i => $"P{i:D2}").ToList();
        foreach (var name in Enumerable.Reverse(names))
        {
            await CreateProductAsync(http, name, $"SKU-{name}");
        }

        return names;
    }

    // ---- Paging ----------------------------------------------------------------------------

    [Fact]
    public async Task WithoutParameters_ReturnsPageOneWithTwentyItemsAndTheTotal()
    {
        using var tenant = await NewTenantAsync();
        var names = await SeedNumberedAsync(tenant.Owner.Client, 25);

        var page = await ListProductsOkAsync(tenant.Owner.Client);

        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);
        Assert.Equal(25, page.TotalCount);
        Assert.Equal(names.Take(20), page.Items.Select(p => p.Name));
    }

    [Fact]
    public async Task Pages_PartitionTheOrderedProductsWithoutGapsOrOverlap()
    {
        using var tenant = await NewTenantAsync();
        var names = await SeedNumberedAsync(tenant.Owner.Client, 25);
        var http = tenant.Owner.Client;

        var first = await ListProductsOkAsync(http, "page=1&pageSize=10");
        var second = await ListProductsOkAsync(http, "page=2&pageSize=10");
        var third = await ListProductsOkAsync(http, "page=3&pageSize=10");
        var beyond = await ListProductsOkAsync(http, "page=4&pageSize=10");

        Assert.Equal(names.Take(10), first.Items.Select(p => p.Name));
        Assert.Equal(names.Skip(10).Take(10), second.Items.Select(p => p.Name));
        Assert.Equal(names.Skip(20), third.Items.Select(p => p.Name));
        Assert.Empty(beyond.Items);
        Assert.All(new[] { first, second, third, beyond }, p => Assert.Equal(25, p.TotalCount));
        Assert.Equal(new[] { 1, 2, 3, 4 }, new[] { first, second, third, beyond }.Select(p => p.Page));
        Assert.All(new[] { first, second, third, beyond }, p => Assert.Equal(10, p.PageSize));
    }

    [Theory]
    [InlineData("page=1&pageSize=1", 1)]
    [InlineData("page=1&pageSize=100", 25)]
    [InlineData("page=2&pageSize=100", 0)]
    [InlineData("page=25&pageSize=1", 1)]
    [InlineData("page=26&pageSize=1", 0)]
    [InlineData("page=2147483647&pageSize=100", 0)]
    [InlineData("page=2147483647&pageSize=1", 0)]
    public async Task ExtremePagingValuesWithinTheAllowedRange_AreAnsweredWithTheRightSliceNeverAnError(string query, int expectedItems)
    {
        using var tenant = await NewTenantAsync();
        await SeedNumberedAsync(tenant.Owner.Client, 25);

        var page = await ListProductsOkAsync(tenant.Owner.Client, query);

        Assert.Equal(expectedItems, page.Items.Count);
        Assert.Equal(25, page.TotalCount);
    }

    public static TheoryData<string, string> InvalidPaging => new()
    {
        { "page=0", "Page" },
        { "page=-1", "Page" },
        { "page=-2147483648", "Page" },
        { "page=abc", "Page" },
        { "page=1.5", "Page" },
        { "page=99999999999", "Page" },
        { "pageSize=0", "PageSize" },
        { "pageSize=-1", "PageSize" },
        { "pageSize=101", "PageSize" },
        { "pageSize=1000", "PageSize" },
        { "pageSize=2147483647", "PageSize" },
        { "pageSize=abc", "PageSize" },
        { "pageSize=1.5", "PageSize" },
        { "pageSize=99999999999", "PageSize" },
    };

    [Theory]
    [MemberData(nameof(InvalidPaging))]
    public async Task OutOfRangeOrMalformedPaging_Returns400ProblemJson_WhileTheValidRequestIsAnswered(string query, string field)
    {
        using var tenant = await NewTenantAsync();
        await CreateProductAsync(tenant.Owner.Client);
        Assert.Equal(1, (await ListProductsOkAsync(tenant.Owner.Client, "page=1&pageSize=1")).TotalCount);

        var response = await ListProductsAsync(tenant.Owner.Client, query);

        await AssertValidationErrorUnderAsync(response, field);
        AssertNoInternalsLeaked(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BothPagingParametersInvalid_ReportsBoth()
    {
        using var tenant = await NewTenantAsync();
        await CreateProductAsync(tenant.Owner.Client);

        var response = await ListProductsAsync(tenant.Owner.Client, "page=0&pageSize=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("page", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pageSize", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnEmptyCatalog_ReturnsAnEmptyPageWithZeroTotal()
    {
        using var tenant = await NewTenantAsync();
        var created = await CreateProductAsync(tenant.Owner.Client);
        Assert.Equal(1, (await ListProductsOkAsync(tenant.Owner.Client)).TotalCount);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteProductAsync(tenant.Owner.Client, created.Id)).StatusCode);

        var page = await ListProductsOkAsync(tenant.Owner.Client);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);
    }

    // ---- Ordering --------------------------------------------------------------------------

    [Fact]
    public async Task Items_AreOrderedByNameThenSku_AcrossPageBoundaries()
    {
        using var tenant = await NewTenantAsync();
        var http = tenant.Owner.Client;
        foreach (var (name, sku) in new[] { ("Bravo", "B-2"), ("Alpha", "A-9"), ("Charlie", "C-1"), ("Bravo", "B-1"), ("Alpha", "A-1") })
        {
            await CreateProductAsync(http, name, sku);
        }

        var all = await ListProductsOkAsync(http);
        var pageA = await ListProductsOkAsync(http, "page=1&pageSize=2");
        var pageB = await ListProductsOkAsync(http, "page=2&pageSize=2");
        var pageC = await ListProductsOkAsync(http, "page=3&pageSize=2");

        var expected = new[] { "A-1", "A-9", "B-1", "B-2", "C-1" };
        Assert.Equal(expected, all.Items.Select(p => p.Sku));
        Assert.Equal(expected, pageA.Items.Concat(pageB.Items).Concat(pageC.Items).Select(p => p.Sku));
    }

    // ---- Search ----------------------------------------------------------------------------

    private static readonly (string Name, string Sku)[] SearchCatalog =
    {
        ("Alpha Widget", "WID-001"),
        ("Beta Gadget", "GAD-001"),
        ("100% Cotton", "COT-100"),
        ("Under_score", "UND-001"),
        ("Bracket [x] item", "BRK-001"),
        ("Class [a-z] set", "RNG-001"),
        ("Plain", "PLAIN-1"),
        ("Sku carrier", "SEARCHME-9"),
        ("Nine", "SKU_NINE"),
        ("Back\\slash", "BSL-001"),
    };

    private static readonly ConditionalWeakTable<CustomWebApplicationFactory, Lazy<Task<TestTenant>>> SharedSearchCatalogs = new();

    /// <summary>
    /// One tenant holding <see cref="SearchCatalog"/>, created on first use and shared by every read-only search and
    /// budget test (seeding it per case dominated the runtime). Tests that write must use <see cref="NewTenantAsync"/>.
    /// </summary>
    private Task<TestTenant> SharedSearchCatalogAsync()
        => SharedSearchCatalogs.GetValue(_factory, f => new Lazy<Task<TestTenant>>(() => SeedSearchCatalogAsync(f))).Value;

    private static async Task<TestTenant> SeedSearchCatalogAsync(CustomWebApplicationFactory factory)
    {
        var tenant = await StartTenantAsync(factory, factory.CreateClient(), "list");
        foreach (var (name, sku) in SearchCatalog)
        {
            await CreateProductAsync(tenant.Owner.Client, name, sku);
        }

        return tenant;
    }

    private static IEnumerable<string> Expected(string term)
        => SearchCatalog
            .Where(p => p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || p.Sku.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Sku)
            .Order(StringComparer.Ordinal);

    public static TheoryData<string> SearchTerms => new()
    {
        "widget", "WIDGET", "WiDgEt", "gad-001", "GAD", "searchme", "SEARCHME-9", "a", "e", "-", "plain",
        "%", "100%", "%00", "_", "_s", "u_", "[", "]", "[x]", "[a-z]", "[^a]", "\\", "slash", "back\\", "*", "?", ".", ".*",
        "'", "\"", "';--", "'; DROP TABLE Products;--", "nomatch-zzz", "alpha widget", "ALPHA WIDGET",
    };

    [Theory]
    [MemberData(nameof(SearchTerms))]
    public async Task Search_IsACaseInsensitiveLiteralContainsOnNameOrSku(string term)
    {
        var tenant = await SharedSearchCatalogAsync();

        var result = await ListProductsOkAsync(tenant.Owner.Client, $"pageSize=100&search={Uri.EscapeDataString(term)}");

        var expected = Expected(term).ToList();
        Assert.Equal(expected, result.Items.Select(p => p.Sku).Order(StringComparer.Ordinal));
        Assert.Equal(expected.Count, result.TotalCount);
    }

    [Fact]
    public async Task Search_TermsThatWouldBeWildcardsMatchOnlyTheirLiteralText_AndNeverEverything()
    {
        var tenant = await SharedSearchCatalogAsync();
        var http = tenant.Owner.Client;
        Assert.Equal(SearchCatalog.Length, (await ListProductsOkAsync(http, "pageSize=100")).TotalCount);

        Assert.Equal(new[] { "COT-100" }, (await ListProductsOkAsync(http, "search=%25")).Items.Select(p => p.Sku));
        Assert.Equal(new[] { "SKU_NINE", "UND-001" }, (await ListProductsOkAsync(http, "search=_")).Items.Select(p => p.Sku).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "BRK-001", "RNG-001" }, (await ListProductsOkAsync(http, "search=%5B")).Items.Select(p => p.Sku).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "RNG-001" }, (await ListProductsOkAsync(http, "search=%5Ba-z%5D")).Items.Select(p => p.Sku));
    }

    [Fact]
    public async Task Search_ReportsTheFilteredTotalAndPagesWithinTheMatches()
    {
        var tenant = await SharedSearchCatalogAsync();
        var http = tenant.Owner.Client;
        var matches = Expected("e").ToList();
        Assert.True(matches.Count > 2 && matches.Count < SearchCatalog.Length, "The probe term must match some but not all products.");

        var first = await ListProductsOkAsync(http, "search=e&page=1&pageSize=2");
        var last = await ListProductsOkAsync(http, $"search=e&page={(matches.Count + 1) / 2}&pageSize=2");

        Assert.Equal(matches.Count, first.TotalCount);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(matches.Count, last.TotalCount);
        Assert.Equal(matches.Count % 2 == 0 ? 2 : 1, last.Items.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%20%20%20")]
    public async Task ABlankSearch_AppliesNoFilter(string search)
    {
        var tenant = await SharedSearchCatalogAsync();

        var result = await ListProductsOkAsync(tenant.Owner.Client, $"pageSize=100&search={search}");

        Assert.Equal(SearchCatalog.Length, result.TotalCount);
        Assert.Equal(SearchCatalog.Length, result.Items.Count);
    }

    [Fact]
    public async Task ASearchInjectionAttempt_FindsNothingAndLeavesTheTableIntact()
    {
        var tenant = await SharedSearchCatalogAsync();
        var http = tenant.Owner.Client;

        var attempt = await ListProductsOkAsync(http, $"search={Uri.EscapeDataString("x'; DELETE FROM Products;--")}");

        Assert.Equal(0, attempt.TotalCount);
        Assert.Equal(SearchCatalog.Length, (await ListProductsOkAsync(http, "pageSize=100")).TotalCount);
    }

    [Theory]
    [InlineData(5000)]
    [InlineData(100000)]
    public async Task AVeryLongSearchTerm_IsHandledInAControlledWay(int length)
    {
        var tenant = await SharedSearchCatalogAsync();
        var http = tenant.Owner.Client;
        Assert.Equal(1, (await ListProductsOkAsync(http, "search=widget")).TotalCount);

        var response = await ListProductsAsync(http, $"search={new string('w', length)}");

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.RequestUriTooLong,
            $"Expected 200, 400 or 414, got {(int)response.StatusCode}.");
        if (response.StatusCode == HttpStatusCode.OK)
        {
            Assert.Equal(0, (await response.Content.ReadFromJsonAsync<PagedResponse<ProductResponse>>())!.TotalCount);
        }
    }

    [Theory]
    [InlineData("%00")]
    [InlineData("%EF%BF%BD")]
    [InlineData("%F0%9F%98%80")]
    [InlineData("%C3%BC%C3%9F")]
    [InlineData("%0D%0A")]
    public async Task OddlyEncodedSearchTerms_AreHandledInAControlledWay(string encoded)
    {
        var tenant = await SharedSearchCatalogAsync();
        var http = tenant.Owner.Client;
        Assert.Equal(1, (await ListProductsOkAsync(http, "search=widget")).TotalCount);

        var response = await ListProductsAsync(http, $"search={encoded}");

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest,
            $"Expected 200 or 400, got {(int)response.StatusCode}.");
    }

    // ---- Round-trip budget -------------------------------------------------------------------

    public static TheoryData<string> BudgetedQueries => new()
    {
        "",
        "page=2&pageSize=10",
        "search=widget",
        "search=%25&pageSize=100",
        "page=999&pageSize=100",
    };

    [Theory]
    [MemberData(nameof(BudgetedQueries))]
    public async Task ListRequest_CostsAtMostTwoDatabaseStatements_ForTheOwner(string query)
    {
        var tenant = await SharedSearchCatalogAsync();

        var before = _factory.DbCommands.Count;
        var response = await ListProductsAsync(tenant.Owner.Client, query);
        var statements = _factory.DbCommands.Count - before;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange(statements, 1, 2); // at least one proves the counter sees this request; count + page is the budget
    }

    [Fact]
    public async Task ListRequest_OnAnEmptyCatalog_CostsAtMostTwoDatabaseStatements()
    {
        using var tenant = await NewTenantAsync();
        await CreateProductAsync(tenant.Owner.Client); // route live for this caller
        var before = _factory.DbCommands.Count;
        var response = await ListProductsAsync(tenant.Owner.Client, "search=no-such-product");
        var statements = _factory.DbCommands.Count - before;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange(statements, 1, 2);
    }
}
