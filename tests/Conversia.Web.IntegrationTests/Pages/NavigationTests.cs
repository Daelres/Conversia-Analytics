extern alias ApiHost;

using System.Net;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>The navigation (the tour always, products and users when signed in), the anonymous home page link to the tour, and the page contracts that must survive the new pages.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class NavigationTests(TestApiFactory api)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/register")]
    [InlineData("/demo")]
    public async Task Anonymous_visitors_get_the_tour_link_but_not_products_or_users(string path)
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync(path)).ShouldBe(HttpStatusCode.OK);

        var nav = NavTargets(page);
        Assert.Contains("/demo", nav.Select(l => l.Path));
        Assert.Contains(nav, l => l.Path == "/demo" && l.Text.Length > 0); // the wording is free; a link with no text is not a link
        Assert.DoesNotContain("/products", nav.Select(l => l.Path));
        Assert.DoesNotContain("/users", nav.Select(l => l.Path));
        Assert.Contains("/login", nav.Select(l => l.Path));
        Assert.Contains("/register", nav.Select(l => l.Path));
        Assert.DoesNotContain(page.Document.Forms, PageContract.IsLogoutForm);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/demo")]
    [InlineData("/users")]
    [InlineData("/products")]
    public async Task Signed_in_visitors_get_tour_products_and_users_and_exactly_one_logout_form(string path)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync(path)).ShouldBe(HttpStatusCode.OK);

        var nav = NavTargets(page);
        foreach (var target in new[] { "/demo", "/products", "/users" })
        {
            Assert.Contains(nav, l => l.Path == target && l.Text.Length > 0); // the Spanish wording is free; the English scan lives in SpanishPagesTests
        }

        Assert.DoesNotContain("/login", nav.Select(l => l.Path));
        Assert.DoesNotContain("/register", nav.Select(l => l.Path));
        Assert.NotNull(page.FindLogoutForm()); // exactly one
    }

    [Fact]
    public async Task Every_signed_in_product_page_offers_exactly_one_logout_form()
    {
        var shop = await Shop.CreateAsync(api, products: 1);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, shop.Owner.Email);

        foreach (var (url, status) in new[]
        {
            ("/products", HttpStatusCode.OK),
            (ProductFlows.EditUrl(shop.Primary.Id), HttpStatusCode.OK),
            (ProductFlows.DeleteUrl(shop.Primary.Id), HttpStatusCode.OK),
            (ProductFlows.EditUrl(Guid.NewGuid()), HttpStatusCode.NotFound),
            (ProductFlows.DeleteUrl(Guid.NewGuid()), HttpStatusCode.NotFound),
        })
        {
            var page = (await session.GetAsync(url)).ShouldBe(status);
            Assert.NotNull(page.FindLogoutForm());
            Assert.Contains("/demo", NavTargets(page).Select(l => l.Path));
        }
    }

    [Fact]
    public async Task The_anonymous_home_page_keeps_its_login_and_register_links_and_gains_one_to_the_tour()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);

        var targets = page.Select("a[href]").Select(a => new Uri(WebTestHost.BaseAddress, a.GetAttribute("href")!).AbsolutePath).ToList();
        Assert.Contains("/login", targets);
        Assert.Contains("/register", targets);
        Assert.Contains("/demo", targets);
        Assert.Empty(page.Select(PageContract.ProfileTenantId));
        Assert.Equal(0, host.Api.CallCount);
    }

    [Fact]
    public async Task With_demo_accounts_shown_the_existing_page_contracts_still_hold()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.CreateDemo(api);
        using var anonymous = host.NewSession();
        using var signedIn = await WebFlows.SignedInAsync(host, owner.Email);

        var login = (await anonymous.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
        var register = (await anonymous.GetAsync("/register")).ShouldBe(HttpStatusCode.OK);
        var home = (await anonymous.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        var users = await WebFlows.UsersAsync(signedIn);

        Assert.NotNull(login.FindPasswordForm());
        Assert.NotNull(register.FindPasswordForm());
        Assert.DoesNotContain(home.Document.Forms, PageContract.IsLogoutForm);
        Assert.NotNull(users.FindPasswordForm()); // the create-user form, still the only one with a password input
        Assert.NotNull(users.FindLogoutForm());
        Assert.NotEmpty(DemoFlows.ShortcutForms(login)); // precondition: the demo panel really is on
    }

    private static List<(string Path, string Text)> NavTargets(PageResponse page)
    {
        var nav = page.Document.QuerySelector("nav") ?? throw new Xunit.Sdk.XunitException($"No nav element on {page.RequestUri.PathAndQuery}.");
        return nav.QuerySelectorAll("a[href]")
            .Select(a => (new Uri(WebTestHost.BaseAddress, a.GetAttribute("href")!).AbsolutePath, a.TextContent.Trim()))
            .ToList();
    }
}
