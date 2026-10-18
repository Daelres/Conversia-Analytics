extern alias ApiHost;

using System.Net;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>GET /products: the tenant's products from GET /api/products with ?page=&amp;search= state, pager, search, and who sees management controls.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class ProductsListPageTests(TestApiFactory api)
{
    // ---- authentication gate ----

    [Fact]
    public async Task Anonymous_products_page_redirects_to_login_with_the_return_url_and_reaches_no_api()
    {
        var shop = await Shop.CreateAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await session.GetAsync("/products");

        Assert.Equal("/products", response.ShouldRedirectToLogin());
        Assert.DoesNotContain(shop.Primary.Name, response.Body);
        Assert.Empty(response.Select(PageContract.ProductRow));
        Assert.Equal(0, host.Api.CallCount);
    }

    [Fact]
    public async Task The_query_string_survives_the_login_round_trip()
    {
        var shop = await Shop.CreateAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var redirect = await session.GetAsync("/products?page=2&search=anvil");

        Assert.Equal("/products?page=2&search=anvil", redirect.ShouldRedirectToLogin());
        var afterLogin = await WebFlows.LoginAsync(session, shop.Owner.Email, ApiSeed.StrongPassword, "/products?page=2&search=anvil");
        afterLogin.ShouldRedirectTo("/products?page=2&search=anvil");
        var landed = (await session.FollowAsync(afterLogin)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("anvil", ProductFlows.SearchBoxValue(landed));
    }

    [Fact]
    public async Task Forged_cookies_and_a_bearer_header_do_not_authenticate_against_the_products_page()
    {
        var shop = await Shop.CreateAsync(api);
        using var host = WebTestHost.Create(api);
        using var forgedCookie = host.NewSession();
        using var bearerOnly = host.NewSession();

        var withCookie = await forgedCookie.GetAsync("/products", ("Cookie", ".AspNetCore.Cookies=CfDJ8-not-a-real-protected-ticket"));
        var withBearer = await bearerOnly.GetAsync("/products", ("Authorization", $"Bearer {shop.Owner.Token}"));

        Assert.Equal("/products", withCookie.ShouldRedirectToLogin());
        Assert.Equal("/products", withBearer.ShouldRedirectToLogin());
        Assert.DoesNotContain(shop.Primary.Name, withCookie.Body + withBearer.Body);
        Assert.Equal(0, host.Api.CallCount);
    }

    // ---- listing ----

    [Fact]
    public async Task Owner_sees_exactly_the_tenants_products_with_sku_name_price_and_stock()
    {
        var shop = await Shop.CreateAsync(api, products: 4);
        var stranger = await Shop.CreateAsync(api, products: 2, prefix: "stranger");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var callsBefore = host.Api.CallCount;

        var page = await ProductFlows.ListAsync(session);

        var rows = ProductFlows.Rows(page).ToDictionary(r => r.Id);
        Assert.Equal(shop.Products.Select(p => p.Id).Order(), rows.Keys.Order());
        foreach (var product in shop.Products)
        {
            Assert.Equal(new ProductRowView(product.Id, product.Sku, product.Name, product.Price, product.Stock), rows[product.Id]);
        }

        foreach (var foreign in stranger.Products)
        {
            Assert.DoesNotContain(foreign.Name, page.Body);
            Assert.DoesNotContain(foreign.Sku, page.Body);
        }

        // One list call and one profile call, each with the caller's own bearer token.
        var calls = host.Api.Calls.Skip(callsBefore).ToList();
        Assert.InRange(calls.Count, 1, 2);
        var list = Assert.Single(calls, c => c.Uri.AbsolutePath == "/api/products");
        Assert.Equal(HttpMethod.Get, list.Method);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal($"Bearer {Assert.Single(host.Api.IssuedTokens)}", list.Authorization);
        var query = ProductFlows.Query(list);
        Assert.Equal("10", query["pageSize"]);
        Assert.True(query["page"] is null or "1", $"The first render asks for page 1, not '{query["page"]}'.");
        Assert.True(string.IsNullOrWhiteSpace(query["search"]));
        Assert.All(calls, c => Assert.True(c.Uri.AbsolutePath is "/api/products" or "/api/users/me", $"Unexpected API call {c.PathAndQuery}."));

        Assert.NotNull(page.FindLogoutForm());
        WebFlows.AssertNotStored(page);
        WebFlows.AssertNoJwtLeaked(session, host);
    }

    [Fact]
    public async Task A_tenant_without_products_gets_an_empty_list_not_an_error()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = await ProductFlows.ListAsync(session);

        Assert.Empty(page.Select(PageContract.ProductRow));
        Assert.Equal(HttpStatusCode.OK, host.Api.Calls.Last(c => c.Uri.AbsolutePath == "/api/products").Status);
        Assert.NotNull(page.Document.QuerySelector(PageContract.SearchForm));
        WebFlows.AssertNoStackTrace(page);
    }

    [Fact]
    public async Task Product_names_are_html_encoded()
    {
        const string hostile = "<img src=x onerror=alert(1)>";
        const string hostileScript = "\"><script>alert(1)</script>&amp;";
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var first = await ApiProducts.CreateAsync(api, owner.Token, hostile);
        var second = await ApiProducts.CreateAsync(api, owner.Token, hostileScript);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = await ProductFlows.ListAsync(session);

        Assert.DoesNotContain("<img src=x", page.Body);
        Assert.DoesNotContain("<script>alert(1)", page.Body);
        Assert.Empty(page.Select("img"));
        Assert.DoesNotContain(page.Document.Scripts, s => s.TextContent.Contains("alert(1)"));
        Assert.Equal(hostile, ProductFlows.Row(page, first.Id).Name);
        Assert.Equal(hostileScript, ProductFlows.Row(page, second.Id).Name);
    }

    // ---- paging (the demo tenants are read-only: Acme has 13 products, Globex 5) ----

    [Fact]
    public async Task Pager_shows_the_page_number_and_count_with_the_total_and_walks_forward_and_back_over_the_tenants_products()
    {
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, DemoAccounts.AcmeOwnerEmail, DemoAccounts.Password);

        var first = await ProductFlows.ListAsync(session);

        var firstPager = first.Document.QuerySelector(PageContract.Pager)!;
        Assert.NotNull(firstPager);
        Assert.Equal("1", firstPager.GetAttribute("data-page"));
        Assert.Equal("2", firstPager.GetAttribute("data-page-count"));
        Assert.Equal("13", firstPager.GetAttribute("data-total-count"));
        AssertPagerSays(firstPager, current: 1, count: 2);
        Assert.Equal(ProductFlows.PageSize, ProductFlows.Rows(first).Count);
        Assert.Null(first.Document.QuerySelector(PageContract.PagerPrevious));
        var next = first.Document.QuerySelector(PageContract.PagerNext);
        Assert.NotNull(next);
        var list = host.Api.Calls.Last(c => c.Uri.AbsolutePath == "/api/products");
        Assert.Equal("10", ProductFlows.Query(list)["pageSize"]);

        var second = (await session.GetAsync(next!.GetAttribute("href")!)).ShouldBe(HttpStatusCode.OK);

        var secondPager = second.Document.QuerySelector(PageContract.Pager)!;
        Assert.Equal("2", secondPager.GetAttribute("data-page"));
        Assert.Equal("2", secondPager.GetAttribute("data-page-count"));
        Assert.Equal("13", secondPager.GetAttribute("data-total-count"));
        AssertPagerSays(secondPager, current: 2, count: 2);
        Assert.Equal(3, ProductFlows.Rows(second).Count);
        Assert.Null(second.Document.QuerySelector(PageContract.PagerNext));
        Assert.NotNull(second.Document.QuerySelector(PageContract.PagerPrevious));
        Assert.Equal("2", ProductFlows.Query(host.Api.Calls.Last(c => c.Uri.AbsolutePath == "/api/products"))["page"]);

        // Together the two pages are exactly Acme's catalog, each product once.
        var skus = ProductFlows.Rows(first).Concat(ProductFlows.Rows(second)).Select(r => r.Sku).ToList();
        Assert.Equal(DemoAccounts.AcmeSkus.Order(), skus.Order());

        var back = (await session.GetAsync(second.Document.QuerySelector(PageContract.PagerPrevious)!.GetAttribute("href")!)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("1", back.Document.QuerySelector(PageContract.Pager)!.GetAttribute("data-page"));
        Assert.Equal(ProductFlows.Rows(first).Select(r => r.Id).Order(), ProductFlows.Rows(back).Select(r => r.Id).Order());
    }

    [Fact]
    public async Task A_single_page_of_products_has_no_previous_or_next_link()
    {
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, DemoAccounts.GlobexOwnerEmail, DemoAccounts.Password);

        var page = await ProductFlows.ListAsync(session);

        var pager = page.Document.QuerySelector(PageContract.Pager)!;
        Assert.NotNull(pager);
        Assert.Equal("1", pager.GetAttribute("data-page"));
        Assert.Equal("1", pager.GetAttribute("data-page-count"));
        Assert.Equal("5", pager.GetAttribute("data-total-count"));
        AssertPagerSays(pager, current: 1, count: 1);
        Assert.Equal(DemoAccounts.GlobexSkus.Order(), ProductFlows.Rows(page).Select(r => r.Sku).Order());
        Assert.Null(page.Document.QuerySelector(PageContract.PagerPrevious));
        Assert.Null(page.Document.QuerySelector(PageContract.PagerNext));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("%20")]
    [InlineData("99999999999999999999")]
    [InlineData("2147483648")]
    public async Task A_blank_or_invalid_page_value_shows_page_one(string pageValue)
    {
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, DemoAccounts.AcmeOwnerEmail, DemoAccounts.Password);
        var pageTwo = await ProductFlows.ListAsync(session, "/products?page=2");
        Assert.Equal("2", pageTwo.Document.QuerySelector(PageContract.Pager)!.GetAttribute("data-page")); // the parameter is honoured when valid
        var callsBefore = host.Api.CallCount;

        var page = await ProductFlows.ListAsync(session, $"/products?page={pageValue}");

        Assert.Equal("1", page.Document.QuerySelector(PageContract.Pager)!.GetAttribute("data-page"));
        Assert.Equal(ProductFlows.PageSize, ProductFlows.Rows(page).Count);
        var list = Assert.Single(host.Api.Calls.Skip(callsBefore), c => c.Uri.AbsolutePath == "/api/products");
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.True(ProductFlows.Query(list)["page"] is null or "1");
    }

    [Theory]
    [InlineData("pageSize=1000")]
    [InlineData("pageSize=1")]
    public async Task The_page_size_is_fixed_and_cannot_be_changed_from_the_url(string sizeParameter)
    {
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, DemoAccounts.AcmeOwnerEmail, DemoAccounts.Password);
        var plain = await ProductFlows.ListAsync(session);
        Assert.Equal(ProductFlows.PageSize, ProductFlows.Rows(plain).Count); // precondition: the tenant has more than one page
        var callsBefore = host.Api.CallCount;

        var page = await ProductFlows.ListAsync(session, $"/products?{sizeParameter}");

        Assert.Equal(ProductFlows.PageSize, ProductFlows.Rows(page).Count);
        Assert.Equal(plain.Document.QuerySelector(PageContract.Pager)!.GetAttribute("data-page-count"), page.Document.QuerySelector(PageContract.Pager)!.GetAttribute("data-page-count"));
        var list = Assert.Single(host.Api.Calls.Skip(callsBefore), c => c.Uri.AbsolutePath == "/api/products");
        Assert.True(ProductFlows.Query(list)["pageSize"] is null || ProductFlows.Query(list)["pageSize"] == ProductFlows.PageSize.ToString());
    }

    [Theory]
    [InlineData("/products?page=999")]
    [InlineData("/products?page=1&page=2")]
    [InlineData("/products?page=2&page=abc")]
    [InlineData("/products?search=a&search=b")]
    public async Task Odd_query_strings_end_in_a_controlled_page(string url)
    {
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, DemoAccounts.AcmeOwnerEmail, DemoAccounts.Password);
        Assert.Equal(ProductFlows.PageSize, ProductFlows.Rows(await ProductFlows.ListAsync(session)).Count); // precondition: the same session lists normally
        var callsBefore = host.Api.CallCount;

        var page = await session.GetAsync(url);

        Assert.True(page.Status < 500, $"Expected a controlled response but got {page.Status}. Page text: {page.Text}");
        WebFlows.AssertNoStackTrace(page);
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Status is null || (int)c.Status >= 500);
        Assert.True(session.HasAuthCookie);
        Assert.NotNull(page.Document.QuerySelector(PageContract.SearchForm));
        Assert.DoesNotContain("GLX-", page.Body); // never another tenant's products
    }

    // ---- search ----

    [Fact]
    public async Task Search_is_a_get_form_that_filters_by_name_or_sku_and_shows_the_term_back()
    {
        var shop = await Shop.CreateAsync(api, products: 3);
        var needle = ApiProducts.UniqueName("Needle");
        var bySku = ApiProducts.UniqueSku("FINDME");
        var byName = await ApiProducts.CreateAsync(api, shop.Owner.Token, needle, ApiProducts.UniqueSku());
        var bySkuProduct = await ApiProducts.CreateAsync(api, shop.Owner.Token, ApiProducts.UniqueName("Other"), bySku);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var all = await ProductFlows.ListAsync(session);
        Assert.Equal(5, ProductFlows.Rows(all).Count); // precondition: all five are listed without a filter

        var nameHit = await ProductFlows.SearchAsync(session, all, needle);
        var skuHit = await ProductFlows.SearchAsync(session, all, bySku);
        var miss = await ProductFlows.SearchAsync(session, all, "no-such-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(new[] { byName.Id }, ProductFlows.Rows(nameHit.ShouldBe(HttpStatusCode.OK)).Select(r => r.Id));
        Assert.Equal(needle, ProductFlows.SearchBoxValue(nameHit));
        Assert.Equal(new[] { bySkuProduct.Id }, ProductFlows.Rows(skuHit.ShouldBe(HttpStatusCode.OK)).Select(r => r.Id));
        Assert.Equal(bySku, ProductFlows.SearchBoxValue(skuHit));
        miss.ShouldBe(HttpStatusCode.OK);
        Assert.Empty(ProductFlows.Rows(miss));
        Assert.NotNull(miss.Document.QuerySelector(PageContract.SearchForm));
        var searched = host.Api.Calls.Where(c => c.Uri.AbsolutePath == "/api/products" && !string.IsNullOrEmpty(ProductFlows.Query(c)["search"])).ToList();
        Assert.Contains(searched, c => ProductFlows.Query(c)["search"] == needle);
        Assert.Contains(searched, c => ProductFlows.Query(c)["search"] == bySku);
        WebFlows.AssertNoStackTrace(miss);
    }

    [Fact]
    public async Task Searching_a_shared_sku_only_ever_finds_the_callers_own_product()
    {
        using var host = WebTestHost.Create(api);
        using var acme = await WebFlows.SignedInAsync(host, DemoAccounts.AcmeOwnerEmail, DemoAccounts.Password);
        using var globex = await WebFlows.SignedInAsync(host, DemoAccounts.GlobexOwnerEmail, DemoAccounts.Password);

        var acmeHit = (await acme.GetAsync(ProductFlows.ListUrl(search: DemoAccounts.SharedSku))).ShouldBe(HttpStatusCode.OK);
        var globexHit = (await globex.GetAsync(ProductFlows.ListUrl(search: DemoAccounts.SharedSku))).ShouldBe(HttpStatusCode.OK);

        var acmeRow = Assert.Single(ProductFlows.Rows(acmeHit));
        var globexRow = Assert.Single(ProductFlows.Rows(globexHit));
        // The seeded names are Spanish and chosen by the seeder, so they are read from the API, not spelled out here;
        // SKU, price and stock are the documented, unchanged values.
        var acmeApi = Assert.Single(await ApiProducts.FindBySkuAsync(api, await ApiProducts.LoginTokenAsync(api, DemoAccounts.AcmeOwnerEmail, DemoAccounts.Password), DemoAccounts.SharedSku));
        var globexApi = Assert.Single(await ApiProducts.FindBySkuAsync(api, await ApiProducts.LoginTokenAsync(api, DemoAccounts.GlobexOwnerEmail, DemoAccounts.Password), DemoAccounts.SharedSku));
        Assert.Equal(("SKU-001", acmeApi.Name, 35.00m, 90), (acmeRow.Sku, acmeRow.Name, acmeRow.Price, acmeRow.Stock));
        Assert.Equal(("SKU-001", globexApi.Name, 15000.00m, 2), (globexRow.Sku, globexRow.Name, globexRow.Price, globexRow.Stock));
        Assert.NotEqual(acmeApi.Name, globexApi.Name);
        Assert.NotEqual(acmeRow.Id, globexRow.Id);
        Assert.Contains(acmeApi.Name, acmeHit.Text); // decoded text: Spanish names with accents are entity-encoded in the raw body
        Assert.Contains(globexApi.Name, globexHit.Text);
        Assert.DoesNotContain(globexApi.Name, acmeHit.Text);
        Assert.DoesNotContain(acmeApi.Name, globexHit.Text);
    }

    [Fact]
    public async Task Paging_through_search_results_keeps_the_search_term()
    {
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, DemoAccounts.AcmeOwnerEmail, DemoAccounts.Password);

        var first = await ProductFlows.ListAsync(session, ProductFlows.ListUrl(search: "ACME-0"));

        Assert.Equal(ProductFlows.PageSize, ProductFlows.Rows(first).Count);
        var pager = first.Document.QuerySelector(PageContract.Pager)!;
        Assert.Equal("12", pager.GetAttribute("data-total-count"));
        Assert.Equal("2", pager.GetAttribute("data-page-count"));
        var nextHref = first.Document.QuerySelector(PageContract.PagerNext)!.GetAttribute("href")!;
        var nextQuery = System.Web.HttpUtility.ParseQueryString(new Uri(first.RequestUri, nextHref).Query);
        Assert.Equal("ACME-0", nextQuery["search"]);
        var second = (await session.GetAsync(nextHref)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(2, ProductFlows.Rows(second).Count);
        Assert.Equal("ACME-0", ProductFlows.SearchBoxValue(second));
        Assert.All(ProductFlows.Rows(first).Concat(ProductFlows.Rows(second)), r => Assert.StartsWith("ACME-0", r.Sku));
    }

    [Theory]
    [InlineData("\"><script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("&amp;&lt;b&gt;'\"")]
    [InlineData("a b&c=d#e+f%41")]
    public async Task The_search_term_is_passed_through_to_the_api_and_shown_back_html_encoded(string term)
    {
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, DemoAccounts.GlobexOwnerEmail, DemoAccounts.Password);
        var callsBefore = host.Api.CallCount;

        var page = await session.GetAsync(ProductFlows.ListUrl(search: term));

        page.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(term, ProductFlows.SearchBoxValue(page));
        Assert.DoesNotContain("<script>alert(1)", page.Body);
        Assert.DoesNotContain("<img src=x", page.Body);
        Assert.Empty(page.Select("img"));
        Assert.DoesNotContain(page.Document.Scripts, s => s.TextContent.Contains("alert(1)"));
        var list = Assert.Single(host.Api.Calls.Skip(callsBefore), c => c.Uri.AbsolutePath == "/api/products");
        Assert.Equal(term, ProductFlows.Query(list)["search"]);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Empty(ProductFlows.Rows(page)); // none of the five Globex products contains these terms
    }

    [Fact]
    public async Task A_search_term_longer_than_the_api_accepts_ends_in_a_controlled_page()
    {
        var term = new string('x', 101);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, DemoAccounts.GlobexOwnerEmail, DemoAccounts.Password);
        (await ProductFlows.ListAsync(session)).ShouldBe(HttpStatusCode.OK);

        var page = await session.GetAsync(ProductFlows.ListUrl(search: term));

        // The requirement does not say what a search the API refuses (400) looks like: a handled page, with no rows and no crash, is all that is pinned.
        Assert.Contains(page.Status, new[] { 200, 400, 503 });
        Assert.Equal("text/html", page.Header("Content-Type")?.Split(';')[0]);
        WebFlows.AssertNoStackTrace(page);
        Assert.Empty(ProductFlows.Rows(page));
        Assert.True(session.HasAuthCookie, "A rejected search is not a reason to sign the user out.");
        Assert.Equal(5, ProductFlows.Rows(await ProductFlows.ListAsync(session)).Count);
    }

    // ---- who sees what ----

    [Fact]
    public async Task Caller_without_products_view_gets_403_not_authorized_and_no_data()
    {
        var shop = await Shop.CreateAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, shop.Owner, "noview", Permission.UsersView);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, member.Email);

        var page = await session.GetAsync("/products");

        page.ShouldBe(HttpStatusCode.Forbidden);
        Assert.Contains(SpanishText.NotAuthorized, page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shop.Primary.Name, page.Body);
        Assert.DoesNotContain(shop.Primary.Sku, page.Body);
        Assert.Empty(page.Select(PageContract.ProductRow));
        Assert.False(ProductFlows.OffersManagement(page));
        Assert.Equal(HttpStatusCode.Forbidden, host.Api.Calls.Last(c => c.Uri.AbsolutePath == "/api/products").Status);
        WebFlows.AssertNoStackTrace(page);
        WebFlows.AssertNotStored(page);
        Assert.True(session.HasAuthCookie, "A refusal is not a reason to sign the user out.");
        (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Api_401_for_an_expired_token_signs_the_user_out_and_sends_them_to_login()
    {
        var shop = await Shop.CreateAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        host.Api.Intercept = request => FakeApi.IsPath(request, "/api/products")
            ? FakeApi.Respond(HttpStatusCode.Unauthorized)
            : Task.FromResult<HttpResponseMessage?>(null);

        var expired = await session.GetAsync("/products");

        expired.ShouldRedirectToLogin();
        Assert.True(WebFlows.ClearsAuthCookie(expired), "The response must clear the authentication cookie.");
        Assert.False(session.HasAuthCookie);
        Assert.DoesNotContain(shop.Primary.Name, expired.Body);
        var callsBefore = host.Api.CallCount;
        (await session.GetAsync("/products")).ShouldRedirectToLogin();
        Assert.Equal(callsBefore, host.Api.CallCount);
    }

    [Theory]
    [MemberData(nameof(BackendFailures))]
    public async Task Products_page_degrades_to_an_unavailable_message_when_the_api_misbehaves(string failure)
    {
        var shop = await Shop.CreateAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        host.Api.Intercept = FakeApi.Failure(failure);

        var page = await session.GetAsync("/products");

        ProductFlows.AssertUnavailable(page);
        Assert.Empty(page.Select(PageContract.ProductRow));
        Assert.Contains(host.Api.Calls, c => c.Uri.AbsolutePath == "/api/products");
        Assert.True(session.HasAuthCookie, "A backend outage is not a reason to sign the user out.");
    }

    public static TheoryData<string> BackendFailures => FakeApi.BackendFailures;

    [Fact]
    public async Task Owner_and_products_manage_holders_get_management_controls_while_view_only_callers_get_none()
    {
        var shop = await Shop.CreateAsync(api, products: 2);
        var viewer = await ApiSeed.CreateMemberAsync(api, shop.Owner, "viewer", Permission.ProductsView);
        var manager = await ApiSeed.CreateMemberAsync(api, shop.Owner, "manager", Permission.ProductsView, Permission.ProductsManage);
        var usersAdmin = await ApiSeed.CreateMemberAsync(api, shop.Owner, "usersadmin", Permission.ProductsView, Permission.UsersView, Permission.UsersManage);
        using var host = WebTestHost.Create(api);
        var expectedLinks = shop.Products.SelectMany(p => new[] { (p.Id, "edit"), (p.Id, "delete") }).OrderBy(l => l.Item1).ThenBy(l => l.Item2).ToList();

        foreach (var email in new[] { shop.Owner.Email, manager.Email })
        {
            using var session = await WebFlows.SignedInAsync(host, email);
            var page = await ProductFlows.ListAsync(session);

            Assert.Single(ProductFlows.CreateForms(page));
            Assert.Equal(expectedLinks, ProductFlows.ManageLinks(page).OrderBy(l => l.Id).ThenBy(l => l.Action).ToList());
        }

        foreach (var email in new[] { viewer.Email, usersAdmin.Email })
        {
            using var session = await WebFlows.SignedInAsync(host, email);
            var page = await ProductFlows.ListAsync(session);

            Assert.Equal(shop.Products.Select(p => p.Id).Order(), ProductFlows.Rows(page).Select(r => r.Id).Order()); // sees the list ...
            Assert.False(ProductFlows.OffersManagement(page), $"{email} may not manage products and must be offered no way to."); // ... and nothing to change it
            Assert.DoesNotContain(ProductFlows.OtherUsableForms(page), f => ProductFlows.IsPost(f));
        }
    }

    [Fact]
    public async Task Management_controls_follow_grants_and_revokes_of_products_manage_on_the_next_render()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var member = await ApiSeed.CreateMemberAsync(api, shop.Owner, "flip", Permission.ProductsView);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, member.Email);
        Assert.False(ProductFlows.OffersManagement(await ProductFlows.ListAsync(session)));

        await ApiSeed.GrantAsync(api, shop.Owner, member.UserId, Permission.ProductsManage);
        var granted = await ProductFlows.ListAsync(session);
        Assert.Single(ProductFlows.CreateForms(granted));
        Assert.Equal(2, ProductFlows.ManageLinks(granted).Count);

        await ApiSeed.RevokeAsync(api, shop.Owner, member.UserId, Permission.ProductsManage);
        var revoked = await ProductFlows.ListAsync(session);
        Assert.False(ProductFlows.OffersManagement(revoked));
        Assert.Single(ProductFlows.Rows(revoked));
    }

    [Fact]
    public async Task The_demo_viewer_sees_the_catalog_and_the_demo_manager_the_controls_without_changing_anything()
    {
        using var host = WebTestHost.Create(api);
        using var viewer = await WebFlows.SignedInAsync(host, DemoAccounts.AcmeViewerEmail, DemoAccounts.Password);
        using var manager = await WebFlows.SignedInAsync(host, DemoAccounts.AcmeManagerEmail, DemoAccounts.Password);
        using var noAccess = await WebFlows.SignedInAsync(host, DemoAccounts.AcmeNoAccessEmail, DemoAccounts.Password);

        var viewerPage = await ProductFlows.ListAsync(viewer);
        var managerPage = await ProductFlows.ListAsync(manager);
        var deniedPage = await noAccess.GetAsync("/products");

        Assert.Equal(ProductFlows.PageSize, ProductFlows.Rows(viewerPage).Count);
        Assert.False(ProductFlows.OffersManagement(viewerPage));
        Assert.Equal(ProductFlows.PageSize, ProductFlows.Rows(managerPage).Count);
        Assert.Single(ProductFlows.CreateForms(managerPage));
        Assert.Equal(ProductFlows.PageSize * 2, ProductFlows.ManageLinks(managerPage).Count);
        deniedPage.ShouldBe(HttpStatusCode.Forbidden);
        Assert.Empty(deniedPage.Select(PageContract.ProductRow));
    }

    // ---- in-page links (the create-panel buttons) ----

    private const string CreatePanelId = "nuevo-producto";

    /// <summary>The links of the page whose href names the create panel by its fragment (found by that fragment, whatever path they carry).</summary>
    private static List<AngleSharp.Dom.IElement> CreatePanelLinks(PageResponse page)
        => page.Select("a[href]").Where(a => a.GetAttribute("href")!.EndsWith("#" + CreatePanelId, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Each link, resolved against the document's base URL, is the page being viewed (path and query) plus the panel fragment, and the panel exists:
    /// a bare <c>#nuevo-producto</c> under <c>&lt;base href="/"&gt;</c> would resolve to <c>/#nuevo-producto</c> and open the home page.
    /// </summary>
    private static void AssertLinksJumpToTheCreatePanel(PageResponse page, IReadOnlyCollection<AngleSharp.Dom.IElement> links)
    {
        Assert.NotNull(page.Document.GetElementById(CreatePanelId));
        Assert.NotEmpty(links);
        foreach (var link in links)
        {
            var href = link.GetAttribute("href");
            var resolved = UiStructureAssertions.Resolve(page, href);
            Assert.True(resolved is not null, $"The href \"{href}\" is not a valid URL.");
            Assert.Equal(page.RequestUri.PathAndQuery + "#" + CreatePanelId, resolved!.PathAndQuery + resolved.Fragment);
            Assert.Null(UiStructureAssertions.InPageLinkProblem(page, href, CreatePanelId));
        }

        var rule = UiStructureAssertions.InPageLinks(new UiPageCase { Label = page.RequestUri.PathAndQuery, Page = page, Type = UiPageType.Products, Authenticated = true }).ToList();
        Assert.True(rule.Count == 0, string.Join(Environment.NewLine, rule));
    }

    [Theory]
    [InlineData("/products")]
    [InlineData("/products?page=2&search=ACME")]
    [InlineData("/products?search=ACME")]
    public async Task The_new_product_button_of_a_filled_list_jumps_to_the_create_panel_of_the_page_being_viewed(string url)
    {
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, DemoAccounts.AcmeOwnerEmail, DemoAccounts.Password);

        var page = await ProductFlows.ListAsync(session, url);

        Assert.NotEmpty(ProductFlows.Rows(page)); // precondition: a filled list, not an empty state
        AssertLinksJumpToTheCreatePanel(page, CreatePanelLinks(page));
    }

    [Theory]
    [InlineData("/products", false)]
    [InlineData("/products?page=2&search=no-such-product", true)]
    [InlineData("/products?search=no-such-product", true)]
    public async Task The_empty_state_call_to_action_also_jumps_to_the_create_panel_of_the_page_being_viewed(string url, bool searching)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var shop = searching ? await Shop.CreateAsync(api, products: 2, prefix: "search") : null;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop?.Owner.Email ?? owner.Email);

        var page = await ProductFlows.ListAsync(session, url);

        Assert.Empty(ProductFlows.Rows(page)); // precondition: an empty list or an empty search
        var emptyState = Assert.Single(page.Select("[data-testid='empty-state']"));
        var links = CreatePanelLinks(page);
        AssertLinksJumpToTheCreatePanel(page, links);
        Assert.Contains(links, link => emptyState.Contains(link)); // the call to action inside the empty state, besides the header button
        Assert.True(links.Count >= 2, $"Expected the header button and the call to action; found {links.Count} link(s) to #{CreatePanelId}.");
    }

    /// <summary>The pager's own text names the current page and the page count (the Spanish wording around them is free), and is not English.</summary>
    private static void AssertPagerSays(AngleSharp.Dom.IElement pager, int current, int count)
    {
        Assert.Matches($@"(?<![\d.,]){current}(?![\d.,]).*(?<![\d.,]){count}(?![\d.,])", pager.TextContent.Replace("\n", " "));
        Assert.Empty(Conversia.TestSupport.EnglishLeaks.Find(pager.TextContent));
    }
}
