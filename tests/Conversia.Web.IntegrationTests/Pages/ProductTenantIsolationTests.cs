extern alias ApiHost;

using System.Net;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>Tenant isolation as a person experiences it: a session only ever sees, opens, changes or deletes its own tenant's products.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class ProductTenantIsolationTests(TestApiFactory api)
{
    [Fact]
    public async Task The_two_demo_tenants_list_exactly_their_own_catalogs()
    {
        using var host = WebTestHost.Create(api);
        using var acme = await WebFlows.SignedInAsync(host, DemoAccounts.AcmeOwnerEmail, DemoAccounts.Password);
        using var globex = await WebFlows.SignedInAsync(host, DemoAccounts.GlobexOwnerEmail, DemoAccounts.Password);

        var acmeRows = await AllRowsAsync(acme);
        var globexRows = await AllRowsAsync(globex);

        Assert.Equal(DemoAccounts.AcmeSkus.Order(), acmeRows.Select(r => r.Sku).Order());
        Assert.Equal(DemoAccounts.GlobexSkus.Order(), globexRows.Select(r => r.Sku).Order());
        Assert.Empty(acmeRows.Select(r => r.Id).Intersect(globexRows.Select(r => r.Id)));
        Assert.Empty(acmeRows.Select(r => r.Name).Intersect(globexRows.Select(r => r.Name)));
    }

    [Fact]
    public async Task Two_registered_tenants_with_the_same_sku_never_see_each_others_product()
    {
        var sku = ApiProducts.UniqueSku("SAME");
        var first = await Shop.CreateAsync(api, products: 1, prefix: "first");
        var second = await Shop.CreateAsync(api, products: 1, prefix: "second");
        var firstProduct = await ApiProducts.CreateAsync(api, first.Owner.Token, ApiProducts.UniqueName("FirstOnly"), sku);
        var secondProduct = await ApiProducts.CreateAsync(api, second.Owner.Token, ApiProducts.UniqueName("SecondOnly"), sku);
        using var host = WebTestHost.Create(api);
        using var firstSession = await WebFlows.SignedInAsync(host, first.Owner.Email);
        using var secondSession = await WebFlows.SignedInAsync(host, second.Owner.Email);

        var firstHit = await ProductFlows.ListAsync(firstSession, ProductFlows.ListUrl(search: sku));
        var secondHit = await ProductFlows.ListAsync(secondSession, ProductFlows.ListUrl(search: sku));
        var firstEdit = await ProductFlows.ListAsync(firstSession, ProductFlows.EditUrl(firstProduct.Id));

        Assert.Equal(firstProduct.Id, Assert.Single(ProductFlows.Rows(firstHit)).Id);
        Assert.Equal(secondProduct.Id, Assert.Single(ProductFlows.Rows(secondHit)).Id);
        Assert.DoesNotContain(secondProduct.Name, firstHit.Body + firstEdit.Body);
        Assert.DoesNotContain(firstProduct.Name, secondHit.Body);
        Assert.Equal(firstProduct.Name, FormModel.From(firstEdit, ProductFlows.FindEditForm(firstEdit)).Get("name"));
    }

    [Fact]
    public async Task Another_tenants_product_id_is_product_not_found_on_the_edit_and_delete_pages()
    {
        var mine = await Shop.CreateAsync(api, products: 1, prefix: "mine");
        var theirs = await Shop.CreateAsync(api, products: 1, prefix: "theirs");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, mine.Owner.Email);
        (await session.GetAsync(ProductFlows.EditUrl(mine.Primary.Id))).ShouldBe(HttpStatusCode.OK); // precondition: my own page opens
        (await session.GetAsync(ProductFlows.DeleteUrl(mine.Primary.Id))).ShouldBe(HttpStatusCode.OK);

        var edit = await session.GetAsync(ProductFlows.EditUrl(theirs.Primary.Id));
        var delete = await session.GetAsync(ProductFlows.DeleteUrl(theirs.Primary.Id));

        ProductFlows.AssertProductNotFound(edit);
        ProductFlows.AssertProductNotFound(delete);
        Assert.DoesNotContain(theirs.Primary.Name, edit.Body + delete.Body);
        Assert.DoesNotContain(theirs.Primary.Sku, edit.Body + delete.Body);
        Assert.Empty(ProductFlows.OtherUsableForms(edit));
        Assert.Empty(ProductFlows.OtherUsableForms(delete));
    }

    [Fact]
    public async Task A_demo_tenant_cannot_open_the_other_demo_tenants_products()
    {
        var acmeToken = await ApiProducts.LoginTokenAsync(api, DemoAccounts.AcmeOwnerEmail, DemoAccounts.Password);
        var acmeProduct = Assert.Single(await ApiProducts.FindBySkuAsync(api, acmeToken, "ACME-001"));
        using var host = WebTestHost.Create(api);
        using var globex = await WebFlows.SignedInAsync(host, DemoAccounts.GlobexOwnerEmail, DemoAccounts.Password);
        Assert.Equal(5, (await AllRowsAsync(globex)).Count); // precondition: Globex's own catalog lists

        var edit = await globex.GetAsync(ProductFlows.EditUrl(acmeProduct.Id));
        var delete = await globex.GetAsync(ProductFlows.DeleteUrl(acmeProduct.Id));

        ProductFlows.AssertProductNotFound(edit);
        ProductFlows.AssertProductNotFound(delete);
        Assert.DoesNotContain(acmeProduct.Name, edit.Text + " " + delete.Text); // decoded text: a Spanish name with accents is entity-encoded in the raw body
        Assert.Equal(acmeProduct.RowVersion, (await ApiProducts.GetAsync(api, acmeToken, acmeProduct.Id)).RowVersion);
    }

    [Fact]
    public async Task A_forged_edit_post_aimed_at_another_tenants_product_changes_nothing_there()
    {
        var mine = await Shop.CreateAsync(api, products: 1, prefix: "mine");
        var theirs = await Shop.CreateAsync(api, products: 1, prefix: "theirs");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, mine.Owner.Email);
        var (_, form) = await ProductFlows.OpenEditFormAsync(session, mine.Primary.Id);
        ProductFlows.FillEdit(form, "Hijacked", "1", "1");
        var forged = ProductFlows.Retarget(form, mine.Primary.Id, theirs.Primary.Id);
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(session, forged);

        ProductFlows.AssertProductNotFound(response);
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Put && c.Status is HttpStatusCode.OK);
        var victim = await ApiProducts.GetAsync(api, theirs.Owner.Token, theirs.Primary.Id);
        Assert.Equal((theirs.Primary.Name, theirs.Primary.RowVersion), (victim.Name, victim.RowVersion));
        var own = await ApiProducts.GetAsync(api, mine.Owner.Token, mine.Primary.Id);
        Assert.Equal((mine.Primary.Name, mine.Primary.RowVersion), (own.Name, own.RowVersion));
        Assert.DoesNotContain(theirs.Primary.Name, response.Body);
    }

    [Fact]
    public async Task A_forged_delete_post_aimed_at_another_tenants_product_deletes_nothing_there()
    {
        var mine = await Shop.CreateAsync(api, products: 1, prefix: "mine");
        var theirs = await Shop.CreateAsync(api, products: 1, prefix: "theirs");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, mine.Owner.Email);
        var (_, form) = await ProductFlows.OpenDeleteFormAsync(session, mine.Primary.Id);
        var forged = ProductFlows.Retarget(form, mine.Primary.Id, theirs.Primary.Id);
        var callsBefore = host.Api.CallCount;

        var response = await ProductFlows.SubmitAsync(session, forged);

        ProductFlows.AssertProductNotFound(response);
        Assert.DoesNotContain(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Delete && c.Status is HttpStatusCode.NoContent);
        Assert.Equal(theirs.Primary.Name, (await ApiProducts.GetAsync(api, theirs.Owner.Token, theirs.Primary.Id)).Name);
        Assert.Equal(mine.Primary.Name, (await ApiProducts.GetAsync(api, mine.Owner.Token, mine.Primary.Id)).Name);
    }

    [Fact]
    public async Task A_member_of_one_tenant_with_manage_rights_still_cannot_reach_another_tenants_products()
    {
        var mine = await Shop.CreateAsync(api, products: 1, prefix: "mine");
        var theirs = await Shop.CreateAsync(api, products: 1, prefix: "theirs");
        var manager = await ApiSeed.CreateMemberAsync(api, mine.Owner, "manager", Permission.ProductsView, Permission.ProductsManage);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, manager.Email);

        var list = await ProductFlows.ListAsync(session);
        var edit = await session.GetAsync(ProductFlows.EditUrl(theirs.Primary.Id));

        Assert.Equal(new[] { mine.Primary.Id }, ProductFlows.Rows(list).Select(r => r.Id));
        Assert.DoesNotContain(theirs.Primary.Name, list.Body);
        ProductFlows.AssertProductNotFound(edit);
    }

    private static async Task<List<ProductRowView>> AllRowsAsync(WebSession session)
    {
        var rows = new List<ProductRowView>();
        var page = await ProductFlows.ListAsync(session);
        rows.AddRange(ProductFlows.Rows(page));
        for (var hops = 0; hops < 20 && page.Document.QuerySelector(PageContract.PagerNext) is { } next; hops++)
        {
            page = (await session.GetAsync(next.GetAttribute("href")!)).ShouldBe(HttpStatusCode.OK);
            rows.AddRange(ProductFlows.Rows(page));
        }

        return rows;
    }
}
