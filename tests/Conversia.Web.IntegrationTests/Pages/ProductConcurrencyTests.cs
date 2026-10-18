extern alias ApiHost;

using System.Net;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>Several users working on the same products at once: optimistic concurrency, the unique SKU, and tenants that must never meet.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class ProductConcurrencyTests(TestApiFactory api)
{
    [Fact]
    public async Task Racing_edits_of_one_product_have_exactly_one_winner_and_the_losers_see_the_conflict()
    {
        const int sessions = 3;
        const int rounds = 3;
        var shop = await Shop.CreateAsync(api, products: rounds);
        var managers = new List<ApiSeed.Member>();
        for (var i = 0; i < sessions - 1; i++)
        {
            managers.Add(await ApiSeed.CreateMemberAsync(api, shop.Owner, $"editor{i}", Permission.ProductsView, Permission.ProductsManage));
        }

        using var host = WebTestHost.Create(api);
        var browsers = new List<WebSession> { await WebFlows.SignedInAsync(host, shop.Owner.Email) };
        foreach (var manager in managers)
        {
            browsers.Add(await WebFlows.SignedInAsync(host, manager.Email));
        }

        for (var round = 0; round < rounds; round++)
        {
            var product = shop.Products[round];
            var forms = new List<FormModel>();
            for (var i = 0; i < sessions; i++)
            {
                var (_, form) = await ProductFlows.OpenEditFormAsync(browsers[i], product.Id);
                forms.Add(ProductFlows.FillEdit(form, $"round{round}-session{i}", (100 + i).ToString(), (200 + i).ToString()));
            }

            var callsBefore = host.Api.CallCount;

            var results = await Task.WhenAll(Enumerable.Range(0, sessions).Select(i => ProductFlows.SubmitAsync(browsers[i], forms[i])));

            var winners = Enumerable.Range(0, sessions).Where(i => results[i].Select(PageContract.NoticeSuccess).Any()).ToList();
            var winner = Assert.Single(winners);
            foreach (var loser in Enumerable.Range(0, sessions).Where(i => i != winner))
            {
                ProductFlows.AssertConflictShown(results[loser], product.Id);
            }

            var persisted = await ApiProducts.GetAsync(api, shop.Owner.Token, product.Id);
            Assert.Equal(($"round{round}-session{winner}", 100m + winner, 200 + winner), (persisted.Name, persisted.Price, persisted.Stock));
            var statuses = host.Api.Calls.Skip(callsBefore).Where(c => c.Method == HttpMethod.Put).Select(c => (int)c.Status!.Value).Order().ToList();
            Assert.Equal(new[] { 200, 409, 409 }, statuses);
        }

        foreach (var browser in browsers)
        {
            browser.Dispose();
        }
    }

    [Fact]
    public async Task Racing_creates_of_one_sku_have_exactly_one_confirmation_and_the_others_see_already_exists()
    {
        const int sessions = 6;
        var shop = await Shop.CreateAsync(api, products: 0);
        var sku = ApiProducts.UniqueSku("RACE");
        var members = new List<ApiSeed.Member>();
        for (var i = 0; i < sessions - 1; i++)
        {
            members.Add(await ApiSeed.CreateMemberAsync(api, shop.Owner, $"creator{i}", Permission.ProductsView, Permission.ProductsManage));
        }

        using var host = WebTestHost.Create(api);
        var browsers = new List<WebSession> { await WebFlows.SignedInAsync(host, shop.Owner.Email) };
        foreach (var member in members)
        {
            browsers.Add(await WebFlows.SignedInAsync(host, member.Email));
        }

        var forms = new List<FormModel>();
        for (var i = 0; i < sessions; i++)
        {
            var (_, form) = await ProductFlows.OpenCreateFormAsync(browsers[i]);
            forms.Add(ProductFlows.FillCreate(form, sku, $"creator-{i}", (10 + i).ToString(), (20 + i).ToString()));
        }

        var callsBefore = host.Api.CallCount;

        var results = await Task.WhenAll(Enumerable.Range(0, sessions).Select(i => ProductFlows.SubmitAsync(browsers[i], forms[i])));

        var winner = Assert.Single(Enumerable.Range(0, sessions), i => results[i].Select(PageContract.NoticeSuccess).Any());
        foreach (var loser in Enumerable.Range(0, sessions).Where(i => i != winner))
        {
            Assert.True(results[loser].Status < 500, $"Session {loser}: expected a controlled response but got {results[loser].Status}.");
            Assert.Contains(SpanishText.ProductSkuTaken, results[loser].Text, StringComparison.OrdinalIgnoreCase);
            ProductFlows.AssertNoSuccessNotice(results[loser]);
            WebFlows.AssertNoStackTrace(results[loser]);
        }

        var persisted = Assert.Single(await ApiProducts.FindBySkuAsync(api, shop.Owner.Token, sku));
        Assert.Equal(($"creator-{winner}", 10m + winner, 20 + winner), (persisted.Name, persisted.Price, persisted.Stock));
        var statuses = host.Api.Calls.Skip(callsBefore).Where(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath == "/api/products").Select(c => (int)c.Status!.Value).Order().ToList();
        Assert.Equal(new[] { 201, 409, 409, 409, 409, 409 }, statuses);
        foreach (var browser in browsers)
        {
            browser.Dispose();
        }
    }

    [Fact]
    public async Task Racing_creates_of_the_same_sku_in_different_tenants_all_succeed()
    {
        const int tenants = 6;
        var sku = ApiProducts.UniqueSku("MULTI");
        var shops = new List<Shop>();
        for (var i = 0; i < tenants; i++)
        {
            shops.Add(await Shop.CreateAsync(api, products: 0, prefix: $"tenant{i}"));
        }

        using var host = WebTestHost.Create(api);

        var results = await Task.WhenAll(shops.Select(async (shop, i) =>
        {
            using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);
            var (_, form) = await ProductFlows.OpenCreateFormAsync(session);
            await Task.Yield();
            return await ProductFlows.SubmitAsync(session, ProductFlows.FillCreate(form, sku, $"tenant-{i}-item", "1", "1"));
        }));

        for (var i = 0; i < tenants; i++)
        {
            ProductFlows.AssertSuccessNotice(results[i]);
            Assert.Equal($"tenant-{i}-item", Assert.Single(ProductFlows.Rows(results[i])).Name);
            Assert.Single(await ApiProducts.FindBySkuAsync(api, shops[i].Owner.Token, sku));
        }
    }

    [Fact]
    public async Task Interleaved_sessions_of_many_tenants_each_see_only_their_own_products_even_with_one_shared_sku()
    {
        const int tenants = 6;
        const int rounds = 4;
        var sharedSku = ApiProducts.UniqueSku("SHARED");
        var shops = new List<(Shop Shop, Guid SharedId, string SharedName)>();
        for (var i = 0; i < tenants; i++)
        {
            var shop = await Shop.CreateAsync(api, products: 2, prefix: $"iso{i}");
            var name = $"shared-{i}-{Guid.NewGuid():N}";
            var shared = await ApiProducts.CreateAsync(api, shop.Owner.Token, name, sharedSku);
            shops.Add((shop, shared.Id, name));
        }

        using var host = WebTestHost.Create(api);

        await Task.WhenAll(shops.Select(async mine =>
        {
            using var session = await WebFlows.SignedInAsync(host, mine.Shop.Owner.Email);
            var expectedIds = mine.Shop.Products.Select(p => p.Id).Append(mine.SharedId).Order().ToList();
            var foreign = shops.Where(other => other.Shop != mine.Shop).ToList();

            for (var round = 0; round < rounds; round++)
            {
                await Task.Yield();
                var all = await ProductFlows.ListAsync(session);
                var bySku = await ProductFlows.ListAsync(session, ProductFlows.ListUrl(search: sharedSku));

                Assert.Equal(expectedIds, ProductFlows.Rows(all).Select(r => r.Id).Order());
                Assert.Equal(mine.SharedId, Assert.Single(ProductFlows.Rows(bySku)).Id);
                foreach (var other in foreign)
                {
                    var theirIds = other.Shop.Products.Select(p => p.Id).Append(other.SharedId);
                    Assert.DoesNotContain(other.SharedName, all.Body + bySku.Body);
                    Assert.DoesNotContain(other.Shop.Products[0].Name, all.Body + bySku.Body);
                    Assert.All(theirIds, id => Assert.DoesNotContain(id.ToString(), all.Body + bySku.Body));
                }
            }
        }));

        var tokens = host.Api.IssuedTokens;
        Assert.Equal(tenants, tokens.Distinct().Count());
        var bearersUsed = host.Api.Calls.Where(c => c.Method == HttpMethod.Get && c.Uri.AbsolutePath == "/api/products").Select(c => c.BearerToken).Distinct().ToList();
        Assert.Equal(tokens.Order(), bearersUsed.Order());
    }

    [Fact]
    public async Task An_edit_racing_a_delete_ends_in_a_controlled_state_with_the_product_gone()
    {
        var shop = await Shop.CreateAsync(api, products: 2);
        var product = shop.Primary;
        var manager = await ApiSeed.CreateMemberAsync(api, shop.Owner, "deleter", Permission.ProductsView, Permission.ProductsManage);
        using var host = WebTestHost.Create(api);
        using var editor = await WebFlows.SignedInAsync(host, shop.Owner.Email);
        using var deleter = await WebFlows.SignedInAsync(host, manager.Email);
        var (_, editForm) = await ProductFlows.OpenEditFormAsync(editor, product.Id);
        var (_, deleteForm) = await ProductFlows.OpenDeleteFormAsync(deleter, product.Id);
        ProductFlows.FillEdit(editForm, "Edited while being deleted", "1", "1");

        var results = await Task.WhenAll(ProductFlows.SubmitAsync(editor, editForm), ProductFlows.SubmitAsync(deleter, deleteForm));

        var (edit, delete) = (results[0], results[1]);
        ProductFlows.AssertSuccessNotice(delete); // the product existed when the delete form was issued, and a save never blocks a delete
        Assert.True(
            edit.Select(PageContract.NoticeSuccess).Any() || edit.Status == 404,
            $"The edit either won before the delete or found the product gone, got {edit.Status}: {edit.Text}");
        if (edit.Status == 404)
        {
            ProductFlows.AssertProductNotFound(edit);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await ApiProducts.TryGetAsync(api, shop.Owner.Token, product.Id)).Status);
        Assert.Equal(shop.Products[1].Id, Assert.Single(await ApiProducts.ListAsync(api, shop.Owner.Token)).Id);
        Assert.DoesNotContain(host.Api.Calls, c => c.Status is null || (int)c.Status >= 500);
    }
}
