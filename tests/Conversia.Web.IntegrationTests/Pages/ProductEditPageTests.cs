extern alias ApiHost;

using System.Globalization;
using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>GET/POST /products/{id}/edit: prefilled form, optimistic concurrency through the hidden base64 rowVersion, not-found and not-authorized outcomes.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class ProductEditPageTests(TestApiFactory api)
{
    // ---- gate and ids ----

    [Fact]
    public async Task Anonymous_edit_page_redirects_to_login_with_the_return_url_and_reaches_no_api()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await session.GetAsync(ProductFlows.EditUrl(shop.Primary.Id));

        Assert.Equal(ProductFlows.EditUrl(shop.Primary.Id), response.ShouldRedirectToLogin());
        Assert.DoesNotContain(shop.Primary.Name, response.Body);
        Assert.Equal(0, host.Api.CallCount);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("123")]
    [InlineData("{id}x")]
    [InlineData("x{id}")]
    public async Task A_non_guid_id_is_a_404_that_never_reaches_the_products_api(string idText)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        (await session.GetAsync(ProductFlows.EditUrl(shop.Primary.Id))).ShouldBe(HttpStatusCode.OK); // precondition: the page exists for a real id
        var callsBefore = host.Api.CallCount;

        var response = await session.GetAsync($"/products/{idText.Replace("{id}", shop.Primary.Id.ToString())}/edit");

        response.ShouldBe(HttpStatusCode.NotFound);
        WebFlows.AssertNoStackTrace(response);
        Assert.DoesNotContain(shop.Primary.Name, response.Body);
        Assert.Empty(ProductFlows.ProductCalls(host, callsBefore));
        Assert.True(session.HasAuthCookie);
    }

    [Fact]
    public async Task An_unknown_id_is_product_not_found_with_a_404_and_no_data()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        (await session.GetAsync(ProductFlows.EditUrl(shop.Primary.Id))).ShouldBe(HttpStatusCode.OK);

        var response = await session.GetAsync(ProductFlows.EditUrl(Guid.NewGuid()));

        ProductFlows.AssertProductNotFound(response);
        Assert.Empty(ProductFlows.OtherUsableForms(response));
        Assert.Equal(HttpStatusCode.NotFound, host.Api.Calls.Last(c => c.Uri.AbsolutePath.StartsWith("/api/products/")).Status);
        WebFlows.AssertNotStored(response);
        Assert.True(session.HasAuthCookie);
    }

    // ---- the form ----

    [Fact]
    public async Task The_form_is_prefilled_shows_the_sku_read_only_and_carries_the_row_version_in_a_hidden_field()
    {
        var shop = await Shop.CreateAsync(api, products: 2);
        var product = shop.Products[1];
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var callsBefore = host.Api.CallCount;

        var page = (await session.GetAsync(ProductFlows.EditUrl(product.Id))).ShouldBe(HttpStatusCode.OK);

        var element = ProductFlows.FindEditForm(page);
        var form = FormModel.From(page, element);
        Assert.Equal(product.Name, form.Get("name"));
        Assert.Equal(product.Price, ProductFlows.ParsePriceInput(form.Get("price")!));
        Assert.Equal(product.Stock, int.Parse(form.Get("stock")!, CultureInfo.InvariantCulture));
        Assert.Equal(product.RowVersion, form.Get("rowVersion"));
        Assert.Equal("hidden", PageContract.ControlNamed(element, "rowVersion")!.GetAttribute("type"));
        Assert.False(string.IsNullOrEmpty(form.Get(FormModel.AntiforgeryField)), "The form must carry an antiforgery token.");
        Assert.Contains(product.Sku, page.Text);
        var skuControl = PageContract.ControlNamed(element, "sku");
        Assert.True(skuControl is null || skuControl.HasAttribute("readonly") || skuControl.HasAttribute("disabled"), "The SKU cannot be edited.");
        Assert.DoesNotContain(shop.Products[0].Name, page.Body); // only this product
        var calls = host.Api.Calls.Skip(callsBefore).ToList();
        Assert.InRange(calls.Count, 1, 2);
        Assert.Equal(HttpStatusCode.OK, Assert.Single(calls, c => c.Uri.AbsolutePath == $"/api/products/{product.Id}").Status);
        Assert.All(calls, c => Assert.True(c.Uri.AbsolutePath == $"/api/products/{product.Id}" || c.Uri.AbsolutePath == "/api/users/me"));
        Assert.NotNull(page.FindLogoutForm());
        WebFlows.AssertNotStored(page);
    }

    [Fact]
    public async Task A_hostile_product_name_is_html_encoded_in_the_prefilled_form()
    {
        const string hostile = "\"><img src=x onerror=alert(1)>";
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var product = await ApiProducts.CreateAsync(api, owner.Token, hostile);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync(ProductFlows.EditUrl(product.Id))).ShouldBe(HttpStatusCode.OK);

        Assert.DoesNotContain("<img src=x", page.Body);
        Assert.Empty(page.Select("img"));
        Assert.Equal(hostile, FormModel.From(page, ProductFlows.FindEditForm(page)).Get("name"));
    }

    // ---- saving ----

    [Fact]
    public async Task Saving_updates_the_product_redirects_to_the_list_with_a_confirmation_and_keeps_the_sku()
    {
        var shop = await Shop.CreateAsync(api, products: 2);
        var product = shop.Primary;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, product.Id);
        var newName = ApiProducts.UniqueName("Renamed");
        ProductFlows.FillEdit(form, newName, "77.70", "41");
        var callsBefore = host.Api.CallCount;

        var response = await session.SubmitAsync(form);

        Assert.True(response.IsRedirect, $"A successful save redirects back to the list, got {response.Status}. Page text: {response.Text}");
        Assert.Equal("/products", new Uri(WebTestHost.BaseAddress, response.RedirectTarget!).AbsolutePath);
        WebFlows.AssertNotStored(response);
        var landed = await session.FollowAsync(response);
        ProductFlows.AssertLandedOnList(landed);
        ProductFlows.AssertSuccessNotice(landed);
        Assert.Equal(new ProductRowView(product.Id, product.Sku, newName, 77.70m, 41), ProductFlows.Row(landed, product.Id));
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id);
        Assert.Equal((product.Sku, newName, 77.70m, 41), (persisted.Sku, persisted.Name, persisted.Price, persisted.Stock));
        Assert.NotEqual(product.RowVersion, persisted.RowVersion);
        var put = Assert.Single(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Put);
        Assert.Equal($"/api/products/{product.Id}", put.Uri.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal($"Bearer {Assert.Single(host.Api.IssuedTokens)}", put.Authorization);
        using (var sent = JsonDocument.Parse(put.Body!))
        {
            Assert.Equal(product.RowVersion, sent.RootElement.GetProperty("rowVersion").GetString());
        }

        var untouched = await ApiProducts.GetAsync(api, shop.Owner.Token, shop.Products[1].Id);
        Assert.Equal(shop.Products[1].RowVersion, untouched.RowVersion);
        WebFlows.AssertNoJwtLeaked(session, host);
    }

    [Fact]
    public async Task A_holder_of_products_manage_can_save_too()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var manager = await ApiSeed.CreateMemberAsync(api, shop.Owner, "manager", Permission.ProductsView, Permission.ProductsManage);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, manager.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, shop.Primary.Id);

        var landed = await ProductFlows.SubmitAsync(session, ProductFlows.FillEdit(form, "Edited by manager", "1", "1"));

        ProductFlows.AssertSuccessNotice(landed);
        Assert.Equal("Edited by manager", (await ApiProducts.GetAsync(api, shop.Owner.Token, shop.Primary.Id)).Name);
    }

    [Fact]
    public async Task Saving_without_changing_anything_is_a_success()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, shop.Primary.Id);

        var landed = await ProductFlows.SubmitAsync(session, form);

        ProductFlows.AssertLandedOnList(landed);
        ProductFlows.AssertSuccessNotice(landed);
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, shop.Primary.Id);
        Assert.Equal((shop.Primary.Name, shop.Primary.Price, shop.Primary.Stock), (persisted.Name, persisted.Price, persisted.Stock));
    }

    [Fact]
    public async Task A_forged_sku_field_in_the_edit_post_changes_nothing_about_the_sku()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, shop.Primary.Id);
        ProductFlows.FillEdit(form, "Name changed", "2", "2").Add("Input.Sku", "HACKED-SKU").Add("sku", "HACKED-SKU");

        var landed = await ProductFlows.SubmitAsync(session, form);

        Assert.True(landed.Status < 500, $"Expected a controlled response but got {landed.Status}.");
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, shop.Primary.Id);
        Assert.Equal(shop.Primary.Sku, persisted.Sku);
        Assert.Empty(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, "HACKED-SKU"));
    }

    // ---- concurrent modification ----

    [Fact]
    public async Task A_stale_form_shows_modified_by_someone_else_with_a_reload_link_and_overwrites_nothing()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var product = shop.Primary;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, staleForm) = await ProductFlows.OpenEditFormAsync(session, product.Id);
        var theirs = await ApiProducts.UpdateAsync(api, shop.Owner.Token, product, "Their name", 50m, 50);
        ProductFlows.FillEdit(staleForm, "My name", "60", "60");
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(session, staleForm);

        ProductFlows.AssertConflictShown(response, product.Id);
        var put = Assert.Single(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Put);
        Assert.Equal(HttpStatusCode.Conflict, put.Status);
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id);
        Assert.Equal((theirs.Name, theirs.Price, theirs.Stock, theirs.RowVersion), (persisted.Name, persisted.Price, persisted.Stock, persisted.RowVersion));
        Assert.True(session.HasAuthCookie, "A conflict is not a reason to sign the user out.");

        // The reload link leads to the latest version, whose form saves.
        var reloadHref = ProductFlows.ReloadLink(response, product.Id)!.GetAttribute("href")!;
        var reloaded = (await session.GetAsync(reloadHref)).ShouldBe(HttpStatusCode.OK);
        var freshForm = FormModel.From(reloaded, ProductFlows.FindEditForm(reloaded));
        Assert.Equal("Their name", freshForm.Get("name"));
        Assert.Equal(theirs.RowVersion, freshForm.Get("rowVersion"));
        var saved = await ProductFlows.SubmitAsync(session, ProductFlows.FillEdit(freshForm, "Merged name", "61", "61"));
        ProductFlows.AssertSuccessNotice(saved);
        Assert.Equal("Merged name", (await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id)).Name);
    }

    [Fact]
    public async Task Submitting_the_same_edit_form_twice_conflicts_the_second_time_and_keeps_the_first_save()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, shop.Primary.Id);
        ProductFlows.FillEdit(form, "First save", "5", "5");

        var first = await ProductFlows.SubmitAsync(session, form);
        var afterFirst = await ApiProducts.GetAsync(api, shop.Owner.Token, shop.Primary.Id);
        ProductFlows.FillEdit(form, "Second save", "6", "6");
        var second = await ProductFlows.SubmitAsync(session, form);

        ProductFlows.AssertSuccessNotice(first);
        ProductFlows.AssertConflictShown(second, shop.Primary.Id);
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, shop.Primary.Id);
        Assert.Equal((afterFirst.Name, afterFirst.RowVersion), (persisted.Name, persisted.RowVersion));
        Assert.Equal("First save", persisted.Name);
    }

    [Fact]
    public async Task A_409_from_the_api_is_shown_as_a_conflict_whatever_produced_it()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, shop.Primary.Id);
        ProductFlows.FillEdit(form, "Whatever", "1", "1");
        host.Api.Intercept = request => request.Method == HttpMethod.Put
            ? FakeApi.Respond(HttpStatusCode.Conflict, """{"title":"conflict","status":409}""")
            : Task.FromResult<HttpResponseMessage?>(null);

        var response = await ProductFlows.SubmitAsync(session, form);

        ProductFlows.AssertConflictShown(response, shop.Primary.Id);
    }

    [Fact]
    public async Task A_product_deleted_meanwhile_is_product_not_found_and_is_not_brought_back()
    {
        var shop = await Shop.CreateAsync(api, products: 2);
        var doomed = shop.Primary;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, doomed.Id);
        await ApiProducts.DeleteAsync(api, shop.Owner.Token, doomed.Id);
        ProductFlows.FillEdit(form, "Too late", "1", "1");

        var response = await ProductFlows.SubmitAsync(session, form);

        ProductFlows.AssertProductNotFound(response);
        Assert.Equal((HttpStatusCode.NotFound, null), await ApiProducts.TryGetAsync(api, shop.Owner.Token, doomed.Id));
        Assert.Empty(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, doomed.Sku));
        Assert.Single(await ApiProducts.ListAsync(api, shop.Owner.Token));
    }

    // ---- validation ----

    [Theory]
    [InlineData("name", "")]
    [InlineData("name", "   ")]
    [InlineData("name", "N-201-chars")]
    [InlineData("price", "-1")]
    [InlineData("price", "1.234")]
    [InlineData("price", "1000000000")]
    [InlineData("stock", "-1")]
    public async Task Input_that_breaks_a_product_rule_shows_a_reason_keeps_the_form_and_changes_nothing(string field, string value)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var product = shop.Primary;
        value = value == "N-201-chars" ? new string('n', 201) : value;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, product.Id);
        ProductFlows.FillEdit(form, "Valid name", "10", "10").Set(field, value);
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(session, form);

        Assert.True(response.Status < 500, $"Expected a controlled (<500) response but got {response.Status}. Page text: {response.Text}");
        WebFlows.AssertNoStackTrace(response);
        ProductFlows.AssertNoSuccessNotice(response);
        Assert.NotNull(ProductFlows.FindEditForm(response));
        Assert.True(session.HasAuthCookie);
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id);
        Assert.Equal((product.Name, product.Price, product.Stock, product.RowVersion), (persisted.Name, persisted.Price, persisted.Stock, persisted.RowVersion));
        var body = new
        {
            name = field == "name" ? value : "Valid name",
            price = field == "price" ? decimal.Parse(value, CultureInfo.InvariantCulture) : 10m,
            stock = field == "stock" ? int.Parse(value, CultureInfo.InvariantCulture) : 10,
            rowVersion = product.RowVersion
        };
        await ProductFlows.AssertRejectionShownAsync(api, host, session, response, callsBefore, ProductFlows.EditUrl(product.Id), shop.Owner.Token, HttpMethod.Put, $"/api/products/{product.Id}", body);
    }

    // See ProductCreatePageTests: text that is not a number, and a blank or missing number, must never be silently coerced to a default and saved.
    [Theory]
    [InlineData("price", "abc")]
    [InlineData("price", "")]
    [InlineData("price", "99999999999999999999999999999")]
    [InlineData("stock", "1.5")]
    [InlineData("stock", "")]
    [InlineData("stock", "99999999999999999999")]
    [InlineData("stock", "x")]
    public async Task Numbers_that_cannot_even_be_parsed_end_in_a_controlled_page_and_change_nothing(string field, string value)
        => await AssertNothingChangedAsync(form => form.Set(field, value));

    [Theory]
    [InlineData("price")]
    [InlineData("stock")]
    public async Task A_numeric_field_missing_from_the_posted_form_ends_in_a_controlled_page_and_changes_nothing(string field)
        => await AssertNothingChangedAsync(form => form.Without(field));

    // "1,5" used to be refused; it now means 1.5. What stays pinned: it is never read as 15 (a number the user did not type).
    [Fact]
    public async Task A_comma_decimal_price_is_saved_as_that_decimal_and_never_as_15()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var product = shop.Primary;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, product.Id);
        ProductFlows.FillEdit(form, "Valid name", "1,5", "10");

        var response = await ProductFlows.SubmitAsync(session, form);

        Assert.True(response.Status < 500, $"Expected a controlled (<500) response but got {response.Status}. Page text: {response.Text}");
        WebFlows.AssertNoStackTrace(response);
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id);
        Assert.NotEqual(15m, persisted.Price);
        Assert.Equal(1.5m, persisted.Price);
        ProductFlows.AssertSuccessNotice(response);
        Assert.True(session.HasAuthCookie);
    }

    private async Task AssertNothingChangedAsync(Action<FormModel> corrupt)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var product = shop.Primary;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, product.Id);
        corrupt(ProductFlows.FillEdit(form, "Valid name", "10", "10"));
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(session, form);

        Assert.True(response.Status < 500, $"Expected a controlled (<500) response but got {response.Status}. Page text: {response.Text}");
        WebFlows.AssertNoStackTrace(response);
        ProductFlows.AssertNoSuccessNotice(response);
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id);
        Assert.Equal((product.Name, product.Price, product.Stock, product.RowVersion), (persisted.Name, persisted.Price, persisted.Stock, persisted.RowVersion));
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Put && c.Status == HttpStatusCode.OK);
        Assert.True(session.HasAuthCookie);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all!")]
    [InlineData("AAAA")]
    public async Task A_tampered_row_version_is_refused_in_a_controlled_way_and_changes_nothing(string rowVersion)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var product = shop.Primary;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, product.Id);
        ProductFlows.FillEdit(form, "Tampered", "9", "9").Set("rowVersion", rowVersion);

        var response = await ProductFlows.SubmitAsync(session, form);

        Assert.True(response.Status < 500, $"Expected a controlled (<500) response but got {response.Status}. Page text: {response.Text}");
        WebFlows.AssertNoStackTrace(response);
        ProductFlows.AssertNoSuccessNotice(response);
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id);
        Assert.Equal((product.Name, product.RowVersion), (persisted.Name, persisted.RowVersion));
        Assert.True(session.HasAuthCookie);
    }

    [Fact]
    public async Task The_apis_per_field_validation_messages_are_shown_when_the_api_refuses_a_valid_looking_form()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, shop.Primary.Id);
        ProductFlows.FillEdit(form, "Valid name", "10", "10");
        host.Api.Intercept = request => request.Method == HttpMethod.Put
            ? FakeApi.Validation(("Name", "Stub says: the name is not acceptable."), ("Price", "Stub says: the price is not acceptable."))
            : Task.FromResult<HttpResponseMessage?>(null);

        var response = await ProductFlows.SubmitAsync(session, form);

        Assert.Contains("Stub says: the name is not acceptable.", response.Text);
        Assert.Contains("Stub says: the price is not acceptable.", response.Text);
        ProductFlows.AssertNoSuccessNotice(response);
        Assert.NotNull(ProductFlows.FindEditForm(response));
    }

    // ---- who may edit ----

    [Fact]
    public async Task A_view_only_caller_sees_the_details_without_a_usable_form_or_the_not_authorized_treatment()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var viewer = await ApiSeed.CreateMemberAsync(api, shop.Owner, "viewer", Permission.ProductsView);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, viewer.Email);
        Assert.Single(ProductFlows.Rows(await ProductFlows.ListAsync(session))); // precondition: the viewer sees the product on the list

        var page = await session.GetAsync(ProductFlows.EditUrl(shop.Primary.Id));

        Assert.Contains(page.Status, new[] { 200, 403 });
        Assert.Empty(ProductFlows.OtherUsableForms(page));
        Assert.True(
            page.Text.Contains(shop.Primary.Name, StringComparison.Ordinal) || page.Text.Contains(SpanishText.NotAuthorized, StringComparison.OrdinalIgnoreCase),
            $"The page shows neither the product nor a refusal: {page.Text}");
        if (page.Status == 403)
        {
            Assert.Contains(SpanishText.NotAuthorized, page.Text, StringComparison.OrdinalIgnoreCase);
        }

        ProductFlows.AssertNoSuccessNotice(page);
        WebFlows.AssertNoStackTrace(page);
        WebFlows.AssertNotStored(page);
    }

    [Fact]
    public async Task A_caller_without_products_view_gets_403_not_authorized_and_no_product_data()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var member = await ApiSeed.CreateMemberAsync(api, shop.Owner, "noview", Permission.ProductsManage, Permission.UsersView);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, member.Email);

        var page = await session.GetAsync(ProductFlows.EditUrl(shop.Primary.Id));

        page.ShouldBe(HttpStatusCode.Forbidden);
        Assert.Contains(SpanishText.NotAuthorized, page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shop.Primary.Name, page.Body);
        Assert.Empty(ProductFlows.OtherUsableForms(page));
        Assert.Equal(HttpStatusCode.Forbidden, host.Api.Calls.Last(c => c.Uri.AbsolutePath.StartsWith("/api/products/")).Status);
        WebFlows.AssertNotStored(page);
        Assert.True(session.HasAuthCookie);
    }

    [Fact]
    public async Task A_forged_edit_post_from_a_view_only_caller_changes_nothing_and_is_refused()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var product = shop.Primary;
        var viewer = await ApiSeed.CreateMemberAsync(api, shop.Owner, "viewer", Permission.ProductsView);
        using var host = WebTestHost.Create(api);
        using var ownerSession = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        using var viewerSession = await WebFlows.SignedInAsync(host, viewer.Email);
        var (_, ownerForm) = await ProductFlows.OpenEditFormAsync(ownerSession, product.Id);
        var forged = ProductFlows.FillEdit(ownerForm.WithAntiforgeryToken(await WebFlows.AntiforgeryTokenAsync(viewerSession)), "Forged", "1", "1");
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(viewerSession, forged);

        Assert.Contains(response.Status, new[] { 200, 400, 403 });
        if (response.Status != 400)
        {
            Assert.Contains(SpanishText.NotAuthorized, response.Text, StringComparison.OrdinalIgnoreCase);
        }

        ProductFlows.AssertNoSuccessNotice(response);
        WebFlows.AssertNoStackTrace(response);
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Put && c.Status != HttpStatusCode.Forbidden);
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id);
        Assert.Equal((product.Name, product.RowVersion), (persisted.Name, persisted.RowVersion));
        Assert.True(viewerSession.HasAuthCookie, "A refused action is not a reason to sign the user out.");
    }

    // ---- API failures ----

    [Fact]
    public async Task Api_401_on_the_edit_page_signs_the_user_out_and_sends_them_to_login()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        host.Api.Intercept = request => FakeApi.IsPath(request, $"/api/products/{shop.Primary.Id}")
            ? FakeApi.Respond(HttpStatusCode.Unauthorized)
            : Task.FromResult<HttpResponseMessage?>(null);

        var expired = await session.GetAsync(ProductFlows.EditUrl(shop.Primary.Id));

        expired.ShouldRedirectToLogin();
        Assert.True(WebFlows.ClearsAuthCookie(expired));
        Assert.False(session.HasAuthCookie);
        Assert.DoesNotContain(shop.Primary.Name, expired.Body);
    }

    [Fact]
    public async Task Api_401_on_the_edit_post_signs_the_user_out_and_changes_nothing()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, shop.Primary.Id);
        ProductFlows.FillEdit(form, "Too late", "1", "1");
        host.Api.Intercept = request => request.Method == HttpMethod.Get
            ? Task.FromResult<HttpResponseMessage?>(null)
            : FakeApi.Respond(HttpStatusCode.Unauthorized);

        var landing = await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(form));

        Assert.False(session.HasAuthCookie);
        Assert.Equal("/login", landing.RequestUri.AbsolutePath);
        host.Api.Intercept = null;
        Assert.Equal(shop.Primary.Name, (await ApiProducts.GetAsync(api, shop.Owner.Token, shop.Primary.Id)).Name);
    }

    [Theory]
    [MemberData(nameof(BackendFailures))]
    public async Task The_edit_page_degrades_to_unavailable_not_to_product_not_found_when_the_api_misbehaves(string failure)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        host.Api.Intercept = FakeApi.Failure(failure);

        var page = await session.GetAsync(ProductFlows.EditUrl(shop.Primary.Id));

        ProductFlows.AssertUnavailable(page);
        Assert.Empty(ProductFlows.OtherUsableForms(page));
        Assert.True(session.HasAuthCookie);
    }

    [Theory]
    [MemberData(nameof(BackendFailures))]
    public async Task Backend_failures_on_save_show_unavailable_without_crashing_or_signing_out(string failure)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var product = shop.Primary;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, product.Id);
        ProductFlows.FillEdit(form, "During outage", "1", "1");
        host.Api.Intercept = FakeApi.Failure(failure);

        var response = await ProductFlows.SubmitAsync(session, form);

        ProductFlows.AssertUnavailable(response);
        Assert.False(PageResponse.ContainsJwt(response.Body));
        Assert.True(session.HasAuthCookie, "A backend outage is not a reason to sign the user out.");
        host.Api.Intercept = null;
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id);
        Assert.Equal((product.Name, product.RowVersion), (persisted.Name, persisted.RowVersion));
        (await session.GetAsync(ProductFlows.EditUrl(product.Id))).ShouldBe(HttpStatusCode.OK);
    }

    public static TheoryData<string> BackendFailures => FakeApi.RepresentativeBackendFailures; // the full matrix runs on the list page; the mapping itself is unit-tested in ApiClientTests
}
