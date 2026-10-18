extern alias ApiHost;

using System.Net;
using System.Text.Json;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>The create-product form on /products: POST /api/products, post/redirect/get with a confirmation, and every way it can be refused.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class ProductCreatePageTests(TestApiFactory api)
{
    // ---- success ----

    [Fact]
    public async Task Owner_creates_a_product_and_lands_on_the_list_with_a_confirmation_and_the_new_row()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var sku = ApiProducts.UniqueSku("NEW");
        var name = ApiProducts.UniqueName("Created");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(session);
        Assert.False(string.IsNullOrEmpty(form.Get(FormModel.AntiforgeryField)), "The form must carry an antiforgery token.");
        var callsBefore = host.Api.CallCount;

        var response = await session.SubmitAsync(ProductFlows.FillCreate(form, sku, name, "12.50", "7"));

        Assert.True(response.IsRedirect, $"A successful create redirects back to the list, got {response.Status}. Page text: {response.Text}");
        Assert.Equal("/products", new Uri(WebTestHost.BaseAddress, response.RedirectTarget!).AbsolutePath);
        WebFlows.AssertNotStored(response);
        var landed = await session.FollowAsync(response);
        ProductFlows.AssertLandedOnList(landed);
        ProductFlows.AssertSuccessNotice(landed);
        var persisted = Assert.Single(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
        Assert.Equal((name, 12.50m, 7), (persisted.Name, persisted.Price, persisted.Stock));
        Assert.Equal(new ProductRowView(persisted.Id, sku, name, 12.50m, 7), ProductFlows.Row(landed, persisted.Id));
        Assert.Equal(2, ProductFlows.Rows(landed).Count); // the earlier product is still there
        var post = Assert.Single(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath == "/api/products");
        Assert.Equal(HttpStatusCode.Created, post.Status);
        Assert.Equal($"Bearer {Assert.Single(host.Api.IssuedTokens)}", post.Authorization);
        using (var sent = JsonDocument.Parse(post.Body!))
        {
            Assert.Equal(sku, sent.RootElement.GetProperty("sku").GetString());
            Assert.Equal(name, sent.RootElement.GetProperty("name").GetString());
        }

        WebFlows.AssertNoJwtLeaked(session, host);
    }

    [Fact]
    public async Task A_holder_of_products_manage_can_create_too()
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        var manager = await ApiSeed.CreateMemberAsync(api, shop.Owner, "manager", Permission.ProductsView, Permission.ProductsManage);
        var sku = ApiProducts.UniqueSku();
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, manager.Email);

        var landed = await ProductFlows.CreateAsync(session, sku, "Made by manager", 3m, 2);

        ProductFlows.AssertLandedOnList(landed);
        ProductFlows.AssertSuccessNotice(landed);
        Assert.Single(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("999999999.99", "2147483647")]
    [InlineData("0.01", "1")]
    public async Task Boundary_prices_and_stocks_the_api_accepts_are_created(string price, string stock)
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        var sku = ApiProducts.UniqueSku();
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(session);

        var landed = await ProductFlows.SubmitAsync(session, ProductFlows.FillCreate(form, sku, "Boundary", price, stock));

        ProductFlows.AssertSuccessNotice(landed);
        var persisted = Assert.Single(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
        Assert.Equal(decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), persisted.Price);
        Assert.Equal(int.Parse(stock), persisted.Stock);
    }

    [Fact]
    public async Task The_same_sku_may_exist_in_two_tenants()
    {
        var first = await Shop.CreateAsync(api, products: 0, prefix: "first");
        var second = await Shop.CreateAsync(api, products: 0, prefix: "second");
        var sku = ApiProducts.UniqueSku("SHARED");
        using var host = WebTestHost.Create(api);
        using var firstSession = await WebFlows.SignedInAsync(host, first.Owner.Email);
        using var secondSession = await WebFlows.SignedInAsync(host, second.Owner.Email);

        var one = await ProductFlows.CreateAsync(firstSession, sku, "First tenant's", 1m, 1);
        var two = await ProductFlows.CreateAsync(secondSession, sku, "Second tenant's", 2m, 2);

        ProductFlows.AssertSuccessNotice(one);
        ProductFlows.AssertSuccessNotice(two);
        Assert.Equal("First tenant's", Assert.Single(ProductFlows.Rows(one)).Name);
        Assert.Equal("Second tenant's", Assert.Single(ProductFlows.Rows(two)).Name);
    }

    // ---- conflicts and validation ----

    [Fact]
    public async Task A_duplicate_sku_shows_already_exists_inline_and_changes_nothing()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var existing = shop.Primary;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(session);
        var callsBefore = host.Api.CallCount;

        var result = await ProductFlows.SubmitAsync(session, ProductFlows.FillCreate(form, existing.Sku, "Impostor", "99", "99"));

        Assert.True(result.Status < 500, $"Expected a controlled response but got {result.Status}.");
        Assert.Contains(SpanishText.ProductSkuTaken, result.Text, StringComparison.OrdinalIgnoreCase);
        ProductFlows.AssertNoSuccessNotice(result);
        WebFlows.AssertNoStackTrace(result);
        Assert.True(PageContract.IsVisibleAndUsable(ProductFlows.FindCreateForm(result)), "The form stays available to correct the SKU.");
        var attempt = Assert.Single(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath == "/api/products");
        Assert.Equal(HttpStatusCode.Conflict, attempt.Status);
        var persisted = Assert.Single(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, existing.Sku));
        Assert.Equal((existing.Name, existing.Price, existing.Stock, existing.RowVersion), (persisted.Name, persisted.Price, persisted.Stock, persisted.RowVersion));
        Assert.True(session.HasAuthCookie);
    }

    [Fact]
    public async Task Submitting_the_same_form_twice_creates_one_product_and_reports_the_duplicate_the_second_time()
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        var sku = ApiProducts.UniqueSku("TWICE");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(session);
        ProductFlows.FillCreate(form, sku, "Double click", "5", "5");

        var first = await ProductFlows.SubmitAsync(session, form);
        var second = await ProductFlows.SubmitAsync(session, form);

        ProductFlows.AssertSuccessNotice(first);
        Assert.Contains(SpanishText.ProductSkuTaken, second.Text, StringComparison.OrdinalIgnoreCase);
        ProductFlows.AssertNoSuccessNotice(second);
        Assert.Single(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("name", "   ")]
    [InlineData("name", "N-201-chars")]
    [InlineData("sku", "")]
    [InlineData("sku", "has space")]
    [InlineData("sku", "bad/sku!")]
    [InlineData("sku", "S-51-chars")]
    [InlineData("price", "-1")]
    [InlineData("price", "1.234")]
    [InlineData("price", "1000000000")]
    [InlineData("stock", "-1")]
    public async Task Input_that_breaks_a_product_rule_shows_a_reason_keeps_the_form_and_creates_nothing(string field, string value)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var sku = ApiProducts.UniqueSku("RULE");
        value = value switch
        {
            "N-201-chars" => new string('n', 201),
            "S-51-chars" => new string('s', 51),
            _ => value
        };
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(session);
        ProductFlows.FillCreate(form, sku, "Valid name", "10", "10").Set(field, value);
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(session, form);

        Assert.True(response.Status < 500, $"Expected a controlled (<500) response but got {response.Status}. Page text: {response.Text}");
        WebFlows.AssertNoStackTrace(response);
        ProductFlows.AssertNoSuccessNotice(response);
        Assert.Single(ProductFlows.CreateForms(response));
        Assert.True(session.HasAuthCookie, "A rejected form is not a reason to sign the user out.");
        Assert.Empty(WebFlows.IssuedCookies(response));
        Assert.Single(await ApiProducts.ListAsync(api, shop.Owner.Token)); // only the product the shop started with
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Post && c.Status == HttpStatusCode.Created);
        var body = new
        {
            name = field == "name" ? value : "Valid name",
            sku = field == "sku" ? value : sku,
            price = field == "price" ? decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : 10m,
            stock = field == "stock" ? int.Parse(value) : 10
        };
        await ProductFlows.AssertRejectionShownAsync(api, host, session, response, callsBefore, "/products", shop.Owner.Token, HttpMethod.Post, "/api/products", body);
    }

    // The requirement is silent on text that is not a number, and on a number that is blank or missing altogether (static-SSR
    // binding turns both into 0). What is pinned is only that it is never silently turned into a default and saved with a
    // success confirmation.
    [Theory]
    [InlineData("price", "abc")]
    [InlineData("price", "")]
    [InlineData("price", "99999999999999999999999999999")]
    [InlineData("stock", "1.5")]
    [InlineData("stock", "")]
    [InlineData("stock", "99999999999999999999")]
    [InlineData("stock", "x")]
    public async Task Numbers_that_cannot_even_be_parsed_end_in_a_controlled_page_and_create_nothing(string field, string value)
        => await AssertNothingCreatedAsync(form => form.Set(field, value));

    [Theory]
    [InlineData("price")]
    [InlineData("stock")]
    public async Task A_numeric_field_missing_from_the_posted_form_ends_in_a_controlled_page_and_creates_nothing(string field)
        => await AssertNothingCreatedAsync(form => form.Without(field));

    // "1,5" is 1.5 (the comma is a decimal mark in the Spanish interface). It must never be read as 15, a price the user did not type.
    [Fact]
    public async Task A_comma_decimal_price_is_saved_as_that_decimal_and_never_as_15()
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        var sku = ApiProducts.UniqueSku("COMMA");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(session);
        ProductFlows.FillCreate(form, sku, "Valid name", "1,5", "10");

        var response = await ProductFlows.SubmitAsync(session, form);

        Assert.True(response.Status < 500, $"Expected a controlled (<500) response but got {response.Status}. Page text: {response.Text}");
        WebFlows.AssertNoStackTrace(response);
        var created = Assert.Single(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
        Assert.NotEqual(15m, created.Price);
        Assert.Equal(1.5m, created.Price);
        ProductFlows.AssertSuccessNotice(response);
        Assert.True(session.HasAuthCookie);
    }

    private async Task AssertNothingCreatedAsync(Action<FormModel> corrupt)
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        var sku = ApiProducts.UniqueSku("PARSE");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(session);
        corrupt(ProductFlows.FillCreate(form, sku, "Valid name", "10", "10"));
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(session, form);

        Assert.True(response.Status < 500, $"Expected a controlled (<500) response but got {response.Status}. Page text: {response.Text}");
        WebFlows.AssertNoStackTrace(response);
        ProductFlows.AssertNoSuccessNotice(response);
        Assert.Empty(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Post && c.Status == HttpStatusCode.Created);
        Assert.True(session.HasAuthCookie);
        Assert.Single(ProductFlows.CreateForms(response));
    }

    [Fact]
    public async Task The_apis_per_field_validation_messages_are_shown_when_the_api_refuses_a_valid_looking_form()
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        var sku = ApiProducts.UniqueSku("STUB");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(session);
        ProductFlows.FillCreate(form, sku, "Valid name", "10", "10");
        host.Api.Intercept = request => request.Method == HttpMethod.Post && FakeApi.IsPath(request, "/api/products")
            ? FakeApi.Validation(("Name", "Stub says: the name is not acceptable."), ("Sku", "Stub says: the SKU is not acceptable."))
            : Task.FromResult<HttpResponseMessage?>(null);

        var response = await ProductFlows.SubmitAsync(session, form);

        Assert.Contains("Stub says: the name is not acceptable.", response.Text);
        Assert.Contains("Stub says: the SKU is not acceptable.", response.Text);
        ProductFlows.AssertNoSuccessNotice(response);
        Assert.Single(ProductFlows.CreateForms(response));
        host.Api.Intercept = null;
        Assert.Empty(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
    }

    [Fact]
    public async Task A_409_from_the_api_is_shown_as_already_exists_whatever_produced_it()
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(session);
        ProductFlows.FillCreate(form, ApiProducts.UniqueSku("STUB"), "Valid name", "10", "10");
        host.Api.Intercept = request => request.Method == HttpMethod.Post && FakeApi.IsPath(request, "/api/products")
            ? FakeApi.Respond(HttpStatusCode.Conflict, """{"title":"conflict","status":409}""")
            : Task.FromResult<HttpResponseMessage?>(null);

        var response = await ProductFlows.SubmitAsync(session, form);

        Assert.Contains(SpanishText.ProductSkuTaken, response.Text, StringComparison.OrdinalIgnoreCase);
        ProductFlows.AssertNoSuccessNotice(response);
    }

    // ---- who may create ----

    [Fact]
    public async Task A_forged_create_post_from_a_view_only_caller_is_refused_by_the_api_with_an_inline_message()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var viewer = await ApiSeed.CreateMemberAsync(api, shop.Owner, "viewer", Permission.ProductsView);
        var sku = ApiProducts.UniqueSku("FORGED");
        using var host = WebTestHost.Create(api);
        using var ownerSession = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        using var viewerSession = await WebFlows.SignedInAsync(host, viewer.Email);
        var (_, ownerForm) = await ProductFlows.OpenCreateFormAsync(ownerSession);
        Assert.False(ProductFlows.OffersManagement(await ProductFlows.ListAsync(viewerSession))); // precondition: the viewer is offered nothing
        var forged = ProductFlows.FillCreate(ownerForm.WithAntiforgeryToken(await WebFlows.AntiforgeryTokenAsync(viewerSession)), sku, "Forged", "1", "1");
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(viewerSession, forged);

        ProductFlows.AssertNotAuthorized(response);
        var attempt = Assert.Single(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath == "/api/products");
        Assert.Equal(HttpStatusCode.Forbidden, attempt.Status);
        Assert.Empty(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
        Assert.True(viewerSession.HasAuthCookie, "A refused action is not a reason to sign the user out.");
        Assert.False(ProductFlows.OffersManagement(response), "The refusal page must not suddenly offer management controls.");
    }

    [Fact]
    public async Task Api_401_on_the_create_post_signs_the_user_out_and_sends_them_to_login()
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        var sku = ApiProducts.UniqueSku("EXPIRED");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(session);
        ProductFlows.FillCreate(form, sku, "Too late", "1", "1");
        host.Api.Intercept = request => request.Method == HttpMethod.Get
            ? Task.FromResult<HttpResponseMessage?>(null)
            : FakeApi.Respond(HttpStatusCode.Unauthorized);

        var response = await session.SubmitAsync(form);
        var landing = await WebFlows.FollowRedirectsAsync(session, response);

        Assert.False(session.HasAuthCookie, "The expired session's authentication cookie must be cleared.");
        Assert.Equal("/login", landing.RequestUri.AbsolutePath);
        landing.ShouldBe(HttpStatusCode.OK);
        Assert.NotNull(landing.FindPasswordForm());
        host.Api.Intercept = null;
        Assert.Empty(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
        (await session.GetAsync("/products")).ShouldRedirectToLogin();
    }

    [Theory]
    [MemberData(nameof(BackendFailures))]
    public async Task Backend_failures_on_create_show_unavailable_without_crashing_or_signing_out(string failure)
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        var sku = ApiProducts.UniqueSku("OUTAGE");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(session);
        ProductFlows.FillCreate(form, sku, "During outage", "1", "1");
        host.Api.Intercept = FakeApi.Failure(failure);
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(session, form);

        ProductFlows.AssertUnavailable(response);
        Assert.False(PageResponse.ContainsJwt(response.Body));
        Assert.Contains(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Post);
        Assert.True(session.HasAuthCookie, "A backend outage is not a reason to sign the user out.");
        host.Api.Intercept = null;
        Assert.Empty(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
        ProductFlows.AssertLandedOnList(await ProductFlows.ListAsync(session));
    }

    public static TheoryData<string> BackendFailures => FakeApi.RepresentativeBackendFailures;

    [Fact]
    public async Task Anonymous_post_to_the_create_form_with_a_valid_antiforgery_token_reaches_no_api_and_creates_nothing()
    {
        var shop = await Shop.CreateAsync(api, products: 0);
        var sku = ApiProducts.UniqueSku("ANON");
        using var host = WebTestHost.Create(api);
        using var ownerSession = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenCreateFormAsync(ownerSession);
        using var anonymous = host.NewSession();
        var anonymousToken = (await anonymous.GetAsync("/login")).ShouldBe(HttpStatusCode.OK).AntiforgeryToken();
        ProductFlows.FillCreate(form.WithAntiforgeryToken(anonymousToken!), sku, "Anonymous", "1", "1");
        var callsBefore = host.Api.CallCount;

        var response = await anonymous.SubmitAsync(form);

        if (response.IsRedirect)
        {
            response.ShouldRedirectToLogin();
        }
        else
        {
            Assert.InRange(response.Status, 400, 499);
        }

        WebFlows.AssertNoStackTrace(response);
        WebFlows.AssertNoCookieIssued(anonymous, response);
        Assert.Equal(callsBefore, host.Api.CallCount);
        Assert.Empty(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
    }
}
