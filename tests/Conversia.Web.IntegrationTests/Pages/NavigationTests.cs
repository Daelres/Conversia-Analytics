extern alias ApiHost;

using System.Net;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>The navigation (home always; the product areas only when signed in) and the page contracts of the pages that carry it.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class NavigationTests(TestApiFactory api)
{
    private static readonly string[] SignedInTargets = ["/panel", "/conversaciones", "/organizacion", "/users"];

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/register")]
    public async Task Anonymous_visitors_get_the_home_login_and_register_links_but_none_of_the_signed_in_areas(string path)
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync(path)).ShouldBe(HttpStatusCode.OK);

        var nav = NavTargets(page);
        Assert.Contains(nav, l => l.Path == "/" && l.Text.Length > 0); // the wording is free; a link with no text is not a link
        foreach (var target in SignedInTargets)
        {
            Assert.DoesNotContain(target, nav.Select(l => l.Path));
        }

        Assert.Contains("/login", nav.Select(l => l.Path));
        Assert.Contains("/register", nav.Select(l => l.Path));
        Assert.DoesNotContain(page.Document.Forms, PageContract.IsLogoutForm);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/users")]
    [InlineData("/panel")]
    [InlineData("/conversaciones")]
    [InlineData("/organizacion")]
    public async Task Signed_in_visitors_get_every_area_and_exactly_one_logout_form(string path)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync(path)).ShouldBe(HttpStatusCode.OK);

        var nav = NavTargets(page);
        foreach (var target in new[] { "/" }.Concat(SignedInTargets))
        {
            Assert.Contains(nav, l => l.Path == target && l.Text.Length > 0); // the Spanish wording is free; the English scan lives in SpanishPagesTests
        }

        Assert.DoesNotContain("/login", nav.Select(l => l.Path));
        Assert.DoesNotContain("/register", nav.Select(l => l.Path));
        Assert.NotNull(page.FindLogoutForm()); // exactly one
    }

    [Fact]
    public async Task The_anonymous_home_page_keeps_its_login_and_register_links()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);

        var targets = page.Select("a[href]").Select(a => new Uri(WebTestHost.BaseAddress, a.GetAttribute("href")!).AbsolutePath).ToList();
        Assert.Contains("/login", targets);
        Assert.Contains("/register", targets);
        Assert.Empty(page.Select(PageContract.ProfileTenantId));
        Assert.Equal(0, host.Api.CallCount);
    }

    [Fact]
    public async Task The_login_register_and_users_pages_keep_their_form_contracts()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
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
    }

    private static List<(string Path, string Text)> NavTargets(PageResponse page)
    {
        var nav = page.Document.QuerySelector("nav") ?? throw new Xunit.Sdk.XunitException($"No nav element on {page.RequestUri.PathAndQuery}.");
        return nav.QuerySelectorAll("a[href]")
            .Select(a => (new Uri(WebTestHost.BaseAddress, a.GetAttribute("href")!).AbsolutePath, a.TextContent.Trim()))
            .ToList();
    }
}
