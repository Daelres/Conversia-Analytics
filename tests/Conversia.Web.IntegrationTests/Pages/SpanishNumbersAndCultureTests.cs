extern alias ApiHost;

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Conversia.TestSupport;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>
/// The Web always renders in the <c>es</c> culture, whatever the browser asks for, and prices are displayed the Spanish way
/// (<c>1.234,50</c>) while the raw invariant numbers stay readable in <c>data-price</c> / <c>data-stock</c>.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class SpanishNumbersAndCultureTests(TestApiFactory api)
{
    // ---- the culture is always es ----

    public static TheoryData<string, string> RequestedCultures => new()
    {
        { "Accept-Language", "en-US" },
        { "Accept-Language", "en-US,en;q=0.9" },
        { "Accept-Language", "en-GB" },
        { "Accept-Language", "fr-FR,fr;q=0.8" },
        { "Accept-Language", "de-DE" },
        { "Accept-Language", "ja" },
        { "Accept-Language", "es-AR" },
        { "Accept-Language", "es-MX" },
        { "Accept-Language", "*" },
        { "Accept-Language", "zz-ZZ" },
        { "Cookie", ".AspNetCore.Culture=c%3Den-US%7Cuic%3Den-US" },
    };

    [Theory]
    [MemberData(nameof(RequestedCultures))]
    public async Task Whatever_culture_the_browser_asks_for_the_pages_and_the_prices_are_spanish(string header, string value)
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        var product = await ApiProducts.CreateAsync(api, shop.Owner.Token, "Tornillo hexagonal", ApiProducts.UniqueSku("CUL"), 1234.5m, 12);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var report = new SpanishUiReport();

        var login = (await host.NewSession().GetAsync("/login", (header, value))).ShouldBe(HttpStatusCode.OK);
        var list = (await session.GetAsync("/products", (header, value))).ShouldBe(HttpStatusCode.OK);
        var queryOverride = (await session.GetAsync("/products?culture=en-US&ui-culture=en-US", (header, value))).ShouldBe(HttpStatusCode.OK);

        report.Check($"login with {header}: {value}", login);
        report.Check($"products with {header}: {value}", list);
        report.Check($"products with ?culture=en-US and {header}: {value}", queryOverride);
        report.AssertClean();
        foreach (var page in new[] { list, queryOverride })
        {
            Assert.Contains("1.234,50", ProductFlows.DisplayedCell(page, product.Id, "price"));
            Assert.DoesNotContain("1,234.50", page.Text);
        }
    }

    // ---- displayed price vs raw data attributes ----

    [Fact]
    public async Task Prices_are_displayed_with_the_spanish_culture_and_the_raw_invariant_values_are_in_data_attributes()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var cases = new (decimal Price, int Stock, string Displayed)[]
        {
            (0m, 0, "0,00"),
            (0.5m, 3, "0,50"),
            (7.25m, 1200, "7,25"),
            (19.99m, 1, "19,99"),
            (1234.5m, 20, "1.234,50"),
            (15000m, 2, "15.000,00"),
            (1234567.89m, 7, "1.234.567,89"),
            (999999999.99m, 2147483647, "999.999.999,99"),
        };
        var created = new List<(Conversia.Application.DTOs.Products.ProductResponse Product, string Displayed)>();
        foreach (var (price, stock, displayed) in cases)
        {
            created.Add((await ApiProducts.CreateAsync(api, owner.Token, "Artículo " + displayed, ApiProducts.UniqueSku("NUM"), price, stock), displayed));
        }

        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = await ProductFlows.ListAsync(session);

        foreach (var (product, displayed) in created)
        {
            var row = page.Select(PageContract.ProductRow).Single(r => r.GetAttribute("data-product-id") == product.Id.ToString());
            var price = row.QuerySelector(PageContract.Field("price"))!;
            var stock = row.QuerySelector(PageContract.Field("stock"))!;
            Assert.Equal(product.Price.ToString("0.00", CultureInfo.InvariantCulture), price.GetAttribute("data-price"));
            Assert.Equal(product.Stock.ToString(CultureInfo.InvariantCulture), stock.GetAttribute("data-stock"));
            Assert.Contains(displayed, price.TextContent);
            Assert.DoesNotContain(product.Price.ToString("0.00", CultureInfo.InvariantCulture) + " ", price.TextContent + " "); // not shown the invariant way (except where the two coincide)
        }

        // The numbers survive the round trip through the attributes the helpers read.
        Assert.Equal(created.Select(c => (c.Product.Price, c.Product.Stock)).Order(), ProductFlows.Rows(page).Select(r => (r.Price, r.Stock)).Order());
    }

    [Fact]
    public async Task The_seeded_demo_prices_are_displayed_the_spanish_way_and_their_data_attributes_are_unchanged()
    {
        using var host = WebTestHost.Create(api);
        using var globex = await WebFlows.SignedInAsync(host, DemoAccounts.GlobexOwnerEmail, DemoAccounts.Password);

        var page = await ProductFlows.ListAsync(globex, ProductFlows.ListUrl(search: DemoAccounts.SharedSku));

        var row = Assert.Single(ProductFlows.Rows(page));
        Assert.Equal("15000.00", row.PriceAttribute);
        Assert.Equal("2", row.StockAttribute);
        Assert.Contains("15.000,00", ProductFlows.DisplayedCell(page, row.Id, "price"));
    }

    // ---- the edit form pre-fills a comma decimal ----

    [Fact]
    public async Task The_edit_form_prefills_the_price_with_a_comma_decimal()
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        var product = await ApiProducts.CreateAsync(api, shop.Owner.Token, "Tornillo", ApiProducts.UniqueSku("PRE"), 19.99m, 5);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);

        var (_, form) = await ProductFlows.OpenEditFormAsync(session, product.Id);

        Assert.Equal("19,99", form.Get("price"));
        Assert.Equal("5", form.Get("stock"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("19.99")]
    [InlineData("1234.5")]
    [InlineData("15000")]
    [InlineData("999999999.99")]
    public async Task What_the_edit_form_prefills_is_accepted_back_unchanged_and_never_has_a_thousands_separator(string priceText)
    {
        var price = decimal.Parse(priceText, CultureInfo.InvariantCulture);
        var shop = await Shop.CreateAsync(api, products: 0);
        var product = await ApiProducts.CreateAsync(api, shop.Owner.Token, "Tornillo", ApiProducts.UniqueSku("RND"), price, 5);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, product.Id);

        var prefilled = form.Get("price")!;

        Assert.Matches(@"^\d+(,\d{1,2})?$", prefilled);            // digits, at most one comma, at most two decimals: re-submittable as typed
        Assert.Equal(price, ProductFlows.ParsePriceInput(prefilled));
        var landed = await ProductFlows.SubmitAsync(session, form.Set("name", "Tornillo editado"));
        ProductFlows.AssertSuccessNotice(landed);
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id);
        Assert.Equal(price, persisted.Price);
        Assert.Equal("Tornillo editado", persisted.Name);
    }

    // ---- the price field ----

    public static TheoryData<string, string> AcceptedPrices => new()
    {
        { "19,99", "19.99" },
        { "19.99", "19.99" },
        { "1,5", "1.5" },
        { "1.5", "1.5" },
        { "0,5", "0.5" },
        { "12", "12" },
        { "0", "0" },
        { "999999999,99", "999999999.99" },
    };

    public static TheoryData<string> RefusedPrices => new()
    {
        "1.234,50",
        "1,234.50",
        "",
        "   ",
        "abc",
        "-1",
        "-0,5",
        "1,234",
        "12,345",
        "1.5.5",
        "1,5,5",
        "1,999",
        "1.999",
        "0,001",
        "١٢",
        "١,٥",
        "NaN",
        "Infinity",
        "99999999999999999999999999999",
        "1000000000",
        "12 34",
        "1,5€",
    };

    [Theory]
    [MemberData(nameof(AcceptedPrices))]
    public async Task The_create_form_reads_a_comma_or_a_dot_as_the_decimal_mark(string typed, string expected)
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        var sku = ApiProducts.UniqueSku("OK");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(session);

        var landed = await ProductFlows.SubmitAsync(session, ProductFlows.FillCreate(form, sku, "Tornillo", typed, "3"));

        ProductFlows.AssertSuccessNotice(landed);
        var created = Assert.Single(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), created.Price);
        Assert.Equal(created.Price.ToString("0.00", CultureInfo.InvariantCulture), ProductFlows.Row(landed, created.Id).PriceAttribute);
    }

    [Theory]
    [MemberData(nameof(RefusedPrices))]
    public async Task The_create_form_refuses_a_price_it_cannot_read_unambiguously_shows_why_and_saves_nothing(string typed)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var sku = ApiProducts.UniqueSku("NO");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (pristine, form) = await ProductFlows.OpenCreateFormAsync(session);
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(session, ProductFlows.FillCreate(form, sku, "Tornillo", typed, "3"));

        Assert.True(response.Status < 500, $"Expected a controlled (<500) response but got {response.Status}. Page text: {response.Text}");
        WebFlows.AssertNoStackTrace(response);
        ProductFlows.AssertNoSuccessNotice(response);
        Assert.Empty(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
        Assert.Single(await ApiProducts.ListAsync(api, shop.Owner.Token)); // only the product the shop started with: never saved as 0 or as another number
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Post && c.Status == HttpStatusCode.Created);
        Assert.True(session.HasAuthCookie);
        Assert.Single(ProductFlows.CreateForms(response)); // the form stays, to correct the price
        AssertSpanishMessageShown(pristine, response, typed);
    }

    [Theory]
    [MemberData(nameof(AcceptedPrices))]
    public async Task The_edit_form_reads_a_comma_or_a_dot_as_the_decimal_mark(string typed, string expected)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var product = shop.Primary;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, product.Id);

        var landed = await ProductFlows.SubmitAsync(session, ProductFlows.FillEdit(form, "Tornillo", typed, "3"));

        ProductFlows.AssertSuccessNotice(landed);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), (await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id)).Price);
    }

    [Theory]
    [MemberData(nameof(RefusedPrices))]
    public async Task The_edit_form_refuses_a_price_it_cannot_read_unambiguously_shows_why_and_changes_nothing(string typed)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var product = shop.Primary;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (pristine, form) = await ProductFlows.OpenEditFormAsync(session, product.Id);
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(session, ProductFlows.FillEdit(form, "Tornillo", typed, "3"));

        Assert.True(response.Status < 500, $"Expected a controlled (<500) response but got {response.Status}. Page text: {response.Text}");
        WebFlows.AssertNoStackTrace(response);
        ProductFlows.AssertNoSuccessNotice(response);
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id);
        Assert.Equal((product.Name, product.Price, product.Stock, product.RowVersion), (persisted.Name, persisted.Price, persisted.Stock, persisted.RowVersion));
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Put && c.Status == HttpStatusCode.OK);
        Assert.NotNull(ProductFlows.FindEditForm(response));
        AssertSpanishMessageShown(pristine, response, typed);
    }

    // ---- helpers ----

    /// <summary>
    /// A refusal shows a reason a person can read: the words that appear on the page after the submit and not before it
    /// (apart from what the user typed) are not empty and contain no English wording.
    /// </summary>
    private static void AssertSpanishMessageShown(PageResponse pristine, PageResponse response, string typed)
    {
        var known = Words(pristine.Text).Concat(Words(typed)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = Words(response.Text).Where(w => !known.Contains(w)).ToList();

        Assert.True(added.Count > 0, $"A refused price must show a visible message; the page text did not change. Page text: {response.Text}");
        var message = string.Join(" ", added);
        Assert.Empty(EnglishLeaks.Find(message));
    }

    private static IEnumerable<string> Words(string text) => Regex.Split(text, @"\s+").Where(w => w.Length > 0);
}
