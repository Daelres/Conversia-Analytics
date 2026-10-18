extern alias ApiHost;

using System.Net;
using System.Text;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Security;

/// <summary>Antiforgery on every product POST, no cacheable tenant data, no JWT or exception detail in any product page.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class ProductSecurityTests(TestApiFactory api)
{
    private const string ExceptionMessage = "boom-secret-detail-4711";

    public static TheoryData<string> Tampering => new() { "missing", "garbage", "other-session" };

    public static TheoryData<string, string> TamperedForms
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var form in new[] { "create", "edit", "delete" })
            {
                foreach (var tampering in new[] { "missing", "garbage", "other-session" })
                {
                    data.Add(form, tampering);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(TamperedForms))]
    public async Task Product_forms_with_a_bad_antiforgery_token_are_rejected_before_the_api_and_change_nothing(string which, string tampering)
    {
        var shop = await Shop.CreateAsync(api, products: 2);
        var (editable, deletable) = (shop.Products[0], shop.Products[1]);
        var newSku = ApiProducts.UniqueSku("CSRF");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var form = which switch
        {
            "create" => ProductFlows.FillCreate((await ProductFlows.OpenCreateFormAsync(session)).Form, newSku, "Csrf", "1", "1"),
            "edit" => ProductFlows.FillEdit((await ProductFlows.OpenEditFormAsync(session, editable.Id)).Form, "Csrf", "1", "1"),
            _ => (await ProductFlows.OpenDeleteFormAsync(session, deletable.Id)).Form
        };
        await TamperAsync(form, tampering, host);
        var callsBefore = host.Api.CallCount;

        var response = await session.SubmitAsync(form);

        response.ShouldBe(HttpStatusCode.BadRequest);
        WebFlows.AssertNoStackTrace(response);
        Assert.False(WebFlows.ClearsAuthCookie(response));
        Assert.Equal(callsBefore, host.Api.CallCount);
        Assert.Empty(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, newSku));
        var all = await ApiProducts.ListAsync(api, shop.Owner.Token);
        Assert.Equal(shop.Products.Select(p => (p.Id, p.Name, p.RowVersion)).Order(), all.Select(p => (p.Id, p.Name, p.RowVersion)).Order());
        Assert.True(session.HasAuthCookie);
        ProductFlows.AssertLandedOnList(await ProductFlows.ListAsync(session));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("edit")]
    [InlineData("delete")]
    public async Task Posting_a_non_form_or_empty_body_to_a_product_form_is_rejected_without_calling_the_api(string which)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        var form = which switch
        {
            "create" => (await ProductFlows.OpenCreateFormAsync(session)).Form,
            "edit" => (await ProductFlows.OpenEditFormAsync(session, shop.Primary.Id)).Form,
            _ => (await ProductFlows.OpenDeleteFormAsync(session, shop.Primary.Id)).Form
        };
        var url = form.ActionUri.PathAndQuery;
        var callsBefore = host.Api.CallCount;

        var json = await session.PostRawAsync(url, new StringContent("""{"name":"x","sku":"y","price":1,"stock":1}""", Encoding.UTF8, "application/json"));
        var empty = await session.PostRawAsync(url, new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));

        Assert.InRange(json.Status, 400, 499);
        Assert.InRange(empty.Status, 400, 499);
        WebFlows.AssertNoStackTrace(json);
        WebFlows.AssertNoStackTrace(empty);
        Assert.Equal(callsBefore, host.Api.CallCount);
        var all = await ApiProducts.ListAsync(api, shop.Owner.Token);
        var only = Assert.Single(all);
        Assert.Equal((shop.Primary.Id, shop.Primary.Name, shop.Primary.RowVersion), (only.Id, only.Name, only.RowVersion));
        Assert.True(session.HasAuthCookie);
    }

    [Fact]
    public async Task Authenticated_product_pages_and_their_refusals_are_never_stored()
    {
        var shop = await Shop.CreateAsync(api, products: 2);
        var viewer = await ApiSeed.CreateMemberAsync(api, shop.Owner, "viewer", Permission.ProductsView);
        var noAccess = await ApiSeed.CreateMemberAsync(api, shop.Owner, "noaccess");
        using var host = WebTestHost.Create(api);
        using var owner = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        using var viewerSession = await WebFlows.SignedInAsync(host, viewer.Email);
        using var denied = await WebFlows.SignedInAsync(host, noAccess.Email);

        WebFlows.AssertNotStored((await owner.GetAsync("/products")).ShouldBe(HttpStatusCode.OK));
        WebFlows.AssertNotStored((await owner.GetAsync(ProductFlows.ListUrl(page: 2, search: "x"))).ShouldBe(HttpStatusCode.OK));
        WebFlows.AssertNotStored((await owner.GetAsync(ProductFlows.EditUrl(shop.Primary.Id))).ShouldBe(HttpStatusCode.OK));
        WebFlows.AssertNotStored((await owner.GetAsync(ProductFlows.DeleteUrl(shop.Primary.Id))).ShouldBe(HttpStatusCode.OK));
        WebFlows.AssertNotStored((await owner.GetAsync(ProductFlows.EditUrl(Guid.NewGuid()))).ShouldBe(HttpStatusCode.NotFound));
        WebFlows.AssertNotStored((await owner.GetAsync(ProductFlows.DeleteUrl(Guid.NewGuid()))).ShouldBe(HttpStatusCode.NotFound));
        WebFlows.AssertNotStored((await denied.GetAsync("/products")).ShouldBe(HttpStatusCode.Forbidden));
        WebFlows.AssertNotStored(await viewerSession.GetAsync(ProductFlows.DeleteUrl(shop.Primary.Id)));
        var (_, createForm) = await ProductFlows.OpenCreateFormAsync(owner);
        WebFlows.AssertNotStored(await owner.SubmitAsync(ProductFlows.FillCreate(createForm, ApiProducts.UniqueSku(), "Cache", "1", "1")));
        var (_, editForm) = await ProductFlows.OpenEditFormAsync(owner, shop.Primary.Id);
        WebFlows.AssertNotStored(await owner.SubmitAsync(ProductFlows.FillEdit(editForm, "Cache edit", "1", "1")));
        var (_, deleteForm) = await ProductFlows.OpenDeleteFormAsync(owner, shop.Products[1].Id);
        WebFlows.AssertNotStored(await owner.SubmitAsync(deleteForm));
        using var anonymous = host.NewSession();
        WebFlows.AssertNotStored(await anonymous.GetAsync("/products"));
    }

    [Fact]
    public async Task A_whole_product_journey_never_shows_the_jwt_in_a_response_cookie_or_log()
    {
        var email = ApiSeed.UniqueEmail("journey");
        var sku = ApiProducts.UniqueSku("JOURNEY");
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();
        (await WebFlows.RegisterAsync(session, ApiSeed.UniqueTenantName("journey"), email, ApiSeed.StrongPassword)).ShouldRedirectTo("/panel");

        var created = await ProductFlows.CreateAsync(session, sku, "Journey item", 9.99m, 9);
        var id = Assert.Single(ProductFlows.Rows(created)).Id;
        var (_, editForm) = await ProductFlows.OpenEditFormAsync(session, id);
        var edited = await ProductFlows.SubmitAsync(session, ProductFlows.FillEdit(editForm, "Journey item v2", "10", "10"));
        var (_, deleteForm) = await ProductFlows.OpenDeleteFormAsync(session, id);
        var deleted = await ProductFlows.SubmitAsync(session, deleteForm);

        ProductFlows.AssertSuccessNotice(created);
        ProductFlows.AssertSuccessNotice(edited);
        ProductFlows.AssertSuccessNotice(deleted);
        Assert.Empty(ProductFlows.Rows(deleted));
        Assert.NotEmpty(host.Api.IssuedTokens);
        Assert.Contains(session.Transcript, r => r.RequestUri.AbsolutePath == "/products" && r.Body.Contains("Journey item v2"));
        WebFlows.AssertNoJwtLeaked(session, host);
        WebFlows.AssertNoSecretLogged(host, ApiSeed.StrongPassword);
    }

    public static TheoryData<string> Pages => new() { "list", "edit", "delete", "create", "save", "confirm-delete" };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task An_unexpected_exception_never_reaches_the_browser_as_a_stack_trace(string page)
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        FormModel? form = page switch
        {
            "create" => ProductFlows.FillCreate((await ProductFlows.OpenCreateFormAsync(session)).Form, ApiProducts.UniqueSku(), "Boom", "1", "1"),
            "save" => ProductFlows.FillEdit((await ProductFlows.OpenEditFormAsync(session, shop.Primary.Id)).Form, "Boom", "1", "1"),
            "confirm-delete" => (await ProductFlows.OpenDeleteFormAsync(session, shop.Primary.Id)).Form,
            _ => null
        };
        host.Api.Intercept = _ => throw new InvalidOperationException(ExceptionMessage);

        var response = form is not null
            ? await session.SubmitAsync(form)
            : await session.GetAsync(page switch
            {
                "list" => "/products",
                "edit" => ProductFlows.EditUrl(shop.Primary.Id),
                _ => ProductFlows.DeleteUrl(shop.Primary.Id)
            });

        Assert.Contains(host.Api.Calls, c => c.Status is null); // a call reached the failing API
        Assert.Contains(response.Status, new[] { 200, 302, 303, 500, 503 });
        if (!response.IsRedirect)
        {
            Assert.Equal("text/html", response.Header("Content-Type")?.Split(';')[0]);
            Assert.False(string.IsNullOrWhiteSpace(response.Text), $"The {response.Status} response has an empty body.");
        }

        Assert.DoesNotContain(ExceptionMessage, response.Body);
        Assert.DoesNotContain("InvalidOperationException", response.Body);
        WebFlows.AssertNoStackTrace(response);
        Assert.False(PageResponse.ContainsJwt(response.Body));
        host.Api.Intercept = null;
        Assert.Equal(shop.Primary.Name, (await ApiProducts.GetAsync(api, shop.Owner.Token, shop.Primary.Id)).Name);
        Assert.Single(await ApiProducts.ListAsync(api, shop.Owner.Token));
    }

    private static async Task TamperAsync(FormModel form, string tampering, WebTestHost host)
    {
        switch (tampering)
        {
            case "missing":
                form.WithoutAntiforgeryToken();
                break;
            case "garbage":
                form.WithAntiforgeryToken("CfDJ8-this-is-not-a-valid-token");
                break;
            case "other-session":
                using (var other = host.NewSession())
                {
                    var page = (await other.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
                    form.WithAntiforgeryToken(page.AntiforgeryToken()!);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tampering), tampering, null);
        }
    }
}
