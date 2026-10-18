extern alias ApiHost;

using System.Net;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>GET/POST /products/{id}/delete: a confirmation page (GET is safe), a POST that deletes, and every way it can be refused.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class ProductDeletePageTests(TestApiFactory api)
{
    // ---- gate and ids ----

    [Fact]
    public async Task Anonymous_delete_page_redirects_to_login_with_the_return_url_and_reaches_no_api()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await session.GetAsync(ProductFlows.DeleteUrl(shop.Primary.Id));

        Assert.Equal(ProductFlows.DeleteUrl(shop.Primary.Id), response.ShouldRedirectToLogin());
        Assert.DoesNotContain(shop.Primary.Name, response.Body);
        Assert.Equal(0, host.Api.CallCount);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("123")]
    [InlineData("{id}x")]
    public async Task A_non_guid_id_is_a_404_that_never_reaches_the_products_api(string idText)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        (await session.GetAsync(ProductFlows.DeleteUrl(shop.Primary.Id))).ShouldBe(HttpStatusCode.OK); // precondition: the page exists for a real id
        var callsBefore = host.Api.CallCount;

        var response = await session.GetAsync($"/products/{idText.Replace("{id}", shop.Primary.Id.ToString())}/delete");

        response.ShouldBe(HttpStatusCode.NotFound);
        WebFlows.AssertNoStackTrace(response);
        Assert.Empty(ProductFlows.ProductCalls(host, callsBefore));
        Assert.Equal(shop.Primary.Name, (await ApiProducts.GetAsync(api, shop.Owner.Token, shop.Primary.Id)).Name);
    }

    [Fact]
    public async Task An_unknown_id_is_product_not_found_with_a_404()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        (await session.GetAsync(ProductFlows.DeleteUrl(shop.Primary.Id))).ShouldBe(HttpStatusCode.OK);

        var response = await session.GetAsync(ProductFlows.DeleteUrl(Guid.NewGuid()));

        ProductFlows.AssertProductNotFound(response);
        Assert.Empty(ProductFlows.OtherUsableForms(response));
        WebFlows.AssertNotStored(response);
    }

    // ---- confirmation page ----

    [Fact]
    public async Task The_confirmation_page_shows_the_product_and_a_post_button_and_deleting_nothing_by_itself()
    {
        var shop = await Shop.CreateAsync(api, products: 2);
        var product = shop.Primary;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var callsBefore = host.Api.CallCount;

        var (page, form) = await ProductFlows.OpenDeleteFormAsync(session, product.Id);

        Assert.Contains(product.Name, page.Text);
        Assert.Contains(product.Sku, page.Text);
        Assert.False(string.IsNullOrEmpty(form.Get(FormModel.AntiforgeryField)), "The form must carry an antiforgery token.");
        Assert.Null(page.Document.QuerySelector("input[type=password]"));
        Assert.DoesNotContain(shop.Products[1].Name, page.Body);
        var calls = host.Api.Calls.Skip(callsBefore).ToList();
        Assert.InRange(calls.Count, 1, 2);
        Assert.All(calls, c => Assert.Equal(HttpMethod.Get, c.Method));
        Assert.NotNull(page.FindLogoutForm());
        WebFlows.AssertNotStored(page);
        Assert.Equal(2, (await ApiProducts.ListAsync(api, shop.Owner.Token)).Count);
    }

    [Fact]
    public async Task A_hostile_product_name_is_html_encoded_on_the_confirmation_page()
    {
        const string hostile = "<img src=x onerror=alert(1)>";
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var product = await ApiProducts.CreateAsync(api, owner.Token, hostile);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync(ProductFlows.DeleteUrl(product.Id))).ShouldBe(HttpStatusCode.OK);

        Assert.DoesNotContain("<img src=x", page.Body);
        Assert.Empty(page.Select("img"));
        Assert.Contains(hostile, page.Text);
    }

    // ---- deleting ----

    [Fact]
    public async Task Confirming_deletes_the_product_and_redirects_to_the_list_with_a_confirmation()
    {
        var shop = await Shop.CreateAsync(api, products: 3);
        var doomed = shop.Products[1];
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        Assert.Contains(ProductFlows.Rows(await ProductFlows.ListAsync(session)), r => r.Id == doomed.Id); // precondition: it is listed
        var (_, form) = await ProductFlows.OpenDeleteFormAsync(session, doomed.Id);
        var callsBefore = host.Api.CallCount;

        var response = await session.SubmitAsync(form);

        Assert.True(response.IsRedirect, $"A successful delete redirects back to the list, got {response.Status}. Page text: {response.Text}");
        Assert.Equal("/products", new Uri(WebTestHost.BaseAddress, response.RedirectTarget!).AbsolutePath);
        WebFlows.AssertNotStored(response);
        var landed = await session.FollowAsync(response);
        ProductFlows.AssertLandedOnList(landed);
        ProductFlows.AssertSuccessNotice(landed);
        Assert.Equal(shop.Products.Where(p => p.Id != doomed.Id).Select(p => p.Id).Order(), ProductFlows.Rows(landed).Select(r => r.Id).Order());
        Assert.Equal(HttpStatusCode.NotFound, (await ApiProducts.TryGetAsync(api, shop.Owner.Token, doomed.Id)).Status);
        var delete = Assert.Single(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Delete);
        Assert.Equal($"/api/products/{doomed.Id}", delete.Uri.AbsolutePath);
        Assert.Equal(HttpStatusCode.NoContent, delete.Status);
        Assert.Equal($"Bearer {Assert.Single(host.Api.IssuedTokens)}", delete.Authorization);
        WebFlows.AssertNoJwtLeaked(session, host);
    }

    [Fact]
    public async Task A_holder_of_products_manage_can_delete_too()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var manager = await ApiSeed.CreateMemberAsync(api, shop.Owner, "manager", Permission.ProductsView, Permission.ProductsManage);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, manager.Email);
        var (_, form) = await ProductFlows.OpenDeleteFormAsync(session, shop.Primary.Id);

        var landed = await ProductFlows.SubmitAsync(session, form);

        ProductFlows.AssertSuccessNotice(landed);
        Assert.Equal(HttpStatusCode.NotFound, (await ApiProducts.TryGetAsync(api, shop.Owner.Token, shop.Primary.Id)).Status);
    }

    [Fact]
    public async Task Confirming_a_second_time_is_product_not_found_with_a_404()
    {
        var shop = await Shop.CreateAsync(api, products: 2);
        var doomed = shop.Primary;
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenDeleteFormAsync(session, doomed.Id);
        ProductFlows.AssertSuccessNotice(await ProductFlows.SubmitAsync(session, form));

        var again = await ProductFlows.SubmitAsync(session, form);

        ProductFlows.AssertProductNotFound(again);
        Assert.Equal(shop.Products[1].Id, Assert.Single(await ApiProducts.ListAsync(api, shop.Owner.Token)).Id); // the other product is untouched
        Assert.True(session.HasAuthCookie);
    }

    [Fact]
    public async Task A_product_deleted_meanwhile_by_someone_else_is_product_not_found_on_confirm_and_on_reload()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenDeleteFormAsync(session, shop.Primary.Id);
        await ApiProducts.DeleteAsync(api, shop.Owner.Token, shop.Primary.Id);

        var confirmed = await ProductFlows.SubmitAsync(session, form);
        var reloaded = await session.GetAsync(ProductFlows.DeleteUrl(shop.Primary.Id));

        ProductFlows.AssertProductNotFound(confirmed);
        ProductFlows.AssertProductNotFound(reloaded);
    }

    [Fact]
    public async Task Two_sessions_confirming_the_same_deletion_at_once_get_exactly_one_success_and_one_not_found()
    {
        var shop = await Shop.CreateAsync(api, products: 2);
        var doomed = shop.Primary;
        var manager = await ApiSeed.CreateMemberAsync(api, shop.Owner, "manager", Permission.ProductsView, Permission.ProductsManage);
        using var host = WebTestHost.Create(api);
        using var first = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        using var second = await WebFlows.SignedInAsync(host, manager.Email);
        var (_, firstForm) = await ProductFlows.OpenDeleteFormAsync(first, doomed.Id);
        var (_, secondForm) = await ProductFlows.OpenDeleteFormAsync(second, doomed.Id);
        var callsBefore = host.Api.CallCount;

        var results = await Task.WhenAll(ProductFlows.SubmitAsync(first, firstForm), ProductFlows.SubmitAsync(second, secondForm));

        var succeeded = results.Where(r => r.Select(PageContract.NoticeSuccess).Any()).ToList();
        var missing = results.Where(r => r.Status == 404).ToList();
        Assert.True(succeeded.Count == 1 && missing.Count == 1, $"Expected one success and one 404, got: {string.Join(" | ", results.Select(r => $"{r.Status} {r.Text}"))}");
        ProductFlows.AssertProductNotFound(missing[0]);
        var deletes = host.Api.Calls.Skip(callsBefore).Where(c => c.Method == HttpMethod.Delete).Select(c => (int)c.Status!.Value).Order().ToList();
        Assert.Equal(new[] { 204, 404 }, deletes);
        Assert.Equal(shop.Products[1].Id, Assert.Single(await ApiProducts.ListAsync(api, shop.Owner.Token)).Id);
    }

    // ---- who may delete ----

    [Fact]
    public async Task A_view_only_caller_gets_a_controlled_not_authorized_page_without_a_usable_form()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var viewer = await ApiSeed.CreateMemberAsync(api, shop.Owner, "viewer", Permission.ProductsView);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, viewer.Email);
        Assert.Single(ProductFlows.Rows(await ProductFlows.ListAsync(session))); // precondition: the viewer sees the product on the list

        var page = await session.GetAsync(ProductFlows.DeleteUrl(shop.Primary.Id));

        ProductFlows.AssertNotAuthorized(page);
        Assert.Empty(ProductFlows.OtherUsableForms(page));
        Assert.DoesNotContain(host.Api.Calls, c => c.Method == HttpMethod.Delete);
        WebFlows.AssertNotStored(page);
        Assert.True(session.HasAuthCookie);
    }

    [Fact]
    public async Task A_caller_without_products_view_gets_403_not_authorized_and_no_product_data()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var member = await ApiSeed.CreateMemberAsync(api, shop.Owner, "noview", Permission.ProductsManage);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, member.Email);

        var page = await session.GetAsync(ProductFlows.DeleteUrl(shop.Primary.Id));

        page.ShouldBe(HttpStatusCode.Forbidden);
        Assert.Contains(SpanishText.NotAuthorized, page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shop.Primary.Name, page.Body);
        Assert.Empty(ProductFlows.OtherUsableForms(page));
    }

    [Fact]
    public async Task A_forged_delete_post_from_a_view_only_caller_is_refused_and_the_product_survives()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        var product = shop.Primary;
        var viewer = await ApiSeed.CreateMemberAsync(api, shop.Owner, "viewer", Permission.ProductsView);
        using var host = WebTestHost.Create(api);
        using var ownerSession = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        using var viewerSession = await WebFlows.SignedInAsync(host, viewer.Email);
        var (_, ownerForm) = await ProductFlows.OpenDeleteFormAsync(ownerSession, product.Id);
        var forged = ownerForm.WithAntiforgeryToken(await WebFlows.AntiforgeryTokenAsync(viewerSession));
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(viewerSession, forged);

        ProductFlows.AssertNotAuthorized(response);
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Delete && c.Status != HttpStatusCode.Forbidden);
        var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id);
        Assert.Equal((product.Name, product.RowVersion), (persisted.Name, persisted.RowVersion));
        Assert.True(viewerSession.HasAuthCookie, "A refused action is not a reason to sign the user out.");
    }

    // ---- API failures ----

    [Fact]
    public async Task Api_401_on_the_delete_page_signs_the_user_out_and_sends_them_to_login()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        host.Api.Intercept = request => FakeApi.IsPath(request, $"/api/products/{shop.Primary.Id}")
            ? FakeApi.Respond(HttpStatusCode.Unauthorized)
            : Task.FromResult<HttpResponseMessage?>(null);

        var expired = await session.GetAsync(ProductFlows.DeleteUrl(shop.Primary.Id));

        expired.ShouldRedirectToLogin();
        Assert.True(WebFlows.ClearsAuthCookie(expired));
        Assert.False(session.HasAuthCookie);
    }

    [Fact]
    public async Task Api_401_on_the_delete_post_signs_the_user_out_and_the_product_survives()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenDeleteFormAsync(session, shop.Primary.Id);
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
    public async Task The_delete_page_degrades_to_unavailable_not_to_product_not_found_when_the_api_misbehaves(string failure)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        host.Api.Intercept = FakeApi.Failure(failure);

        var page = await session.GetAsync(ProductFlows.DeleteUrl(shop.Primary.Id));

        ProductFlows.AssertUnavailable(page);
        Assert.Empty(ProductFlows.OtherUsableForms(page));
        Assert.True(session.HasAuthCookie);
    }

    public static TheoryData<string> BackendFailures => FakeApi.RepresentativeBackendFailures;

    // A 200 (or a 400 page) in answer to a delete is ambiguous for a call whose success is a bodyless 204: only the unambiguous failures here.
    [Theory]
    [InlineData("connection refused")]
    [InlineData("500 problem")]
    public async Task Backend_failures_on_confirm_show_unavailable_without_crashing_or_signing_out(string failure)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var (_, form) = await ProductFlows.OpenDeleteFormAsync(session, shop.Primary.Id);
        host.Api.Intercept = FakeApi.Failure(failure);
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(session, form);

        ProductFlows.AssertUnavailable(response);
        Assert.Contains(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Delete);
        Assert.True(session.HasAuthCookie, "A backend outage is not a reason to sign the user out.");
        host.Api.Intercept = null;
        Assert.Equal(shop.Primary.Name, (await ApiProducts.GetAsync(api, shop.Owner.Token, shop.Primary.Id)).Name);
        (await session.GetAsync(ProductFlows.DeleteUrl(shop.Primary.Id))).ShouldBe(HttpStatusCode.OK);
    }
}
