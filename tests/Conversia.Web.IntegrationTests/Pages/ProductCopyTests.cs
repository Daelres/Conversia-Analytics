extern alias ApiHost;

using System.Net;
using System.Text.RegularExpressions;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>
/// Guards against the wording of the project this code was started from coming back into the UI: every page a visitor can reach
/// (and the signed-in home) must describe Conversia, and none of its visible text, title or meta description may use that vocabulary.
/// <para>
/// "producto/productos" is forbidden too, on purpose: the public copy talks about conversations, advisors and sales, never about a
/// product catalog, so a hit means leftover wording from the removed Products feature. If marketing copy ever needs the word,
/// allow it here with a one-line reason instead of loosening the pattern.
/// </para>
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class ProductCopyTests(TestApiFactory api)
{
    private static readonly Regex Forbidden = new(
        @"(?<![\p{L}\p{N}_])(?:plantillas?|templates?|multi-?espacios?|demos?|recorridos?|acme|skus?|productos?|DR\.Template)(?![\p{L}\p{N}_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static TheoryData<string> PublicPaths => new()
    {
        "/", "/login", "/register", "/admin/login", "/not-found", "/esta-pagina-no-existe", "/Error", "/status/400", "/status/500"
    };

    [Theory]
    [MemberData(nameof(PublicPaths))]
    public async Task Public_pages_do_not_use_template_wording(string path)
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = await session.GetAsync(path);

        AssertNoTemplateWording(path, page);
    }

    [Fact]
    public async Task Signed_in_home_does_not_use_template_wording()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);

        AssertNoTemplateWording("/ (signed in)", page);
    }

    [Fact]
    public async Task The_pending_and_suspended_pages_do_not_use_template_wording()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var pending = await ApiSeed.RegisterOwnerAsync(api, "ferretería", pending: true);
        var suspended = await ApiSeed.RegisterOwnerAsync(api, "droguería");
        await ApiSeed.SetStatusAsync(api, admin, suspended.TenantId, "Suspended");
        using var host = WebTestHost.Create(api);

        foreach (var (label, email) in new[] { ("/cuenta-pendiente (pending)", pending.Email), ("/cuenta-pendiente (suspended)", suspended.Email) })
        {
            using var session = await WebFlows.SignedInAsync(host, email);
            AssertNoTemplateWording(label, (await session.GetAsync("/cuenta-pendiente")).ShouldBe(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task The_administration_pages_do_not_use_template_wording()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api, "ferretería", pending: true);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAdminAsync(host, admin);

        foreach (var path in new[] { "/admin", "/admin/auditoria", $"/admin/empresas/{owner.TenantId}", $"/admin/empresas/{owner.TenantId}/estado/activar" })
        {
            AssertNoTemplateWording(path, (await session.GetAsync(path)).ShouldBe(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task Home_describes_conversia_and_says_it_is_a_test_version()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("Conversia", page.Document.Title);
        Assert.Contains("WhatsApp", page.Text);
        Assert.Contains("Versión de prueba", page.Text);
        Assert.NotEmpty(page.Select("[data-testid=test-version]"));
        var links = page.Select("a[href]").Select(a => new Uri(WebTestHost.BaseAddress, a.GetAttribute("href")!).AbsolutePath).ToList();
        Assert.DoesNotContain(links, l => l.StartsWith("/demo", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/not-found")]
    public async Task Pages_with_the_site_footer_mark_the_test_version(string path)
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = await session.GetAsync(path);

        var footer = Assert.Single(page.Select("footer.site-footer"));
        Assert.Contains("Versión de prueba", footer.TextContent);
        Assert.Contains("Conversia", footer.TextContent);
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/register")]
    [InlineData("/admin/login")]
    public async Task Auth_pages_explain_conversia_in_the_side_panel(string path)
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync(path)).ShouldBe(HttpStatusCode.OK);

        var side = Assert.Single(page.Select("aside.auth-brand"));
        Assert.Contains("WhatsApp", side.TextContent);
        Assert.Contains("Versión de prueba", side.TextContent);
    }

    private static void AssertNoTemplateWording(string label, PageResponse page)
    {
        var hits = SpanishUi.Extract(page)
            .SelectMany(t => Forbidden.Matches(t.Text).Select(m => $"{label}: «{m.Value}» in {t.Where}: {SpanishUi.Snippet(t.Text)}"))
            .ToList();

        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }
}
