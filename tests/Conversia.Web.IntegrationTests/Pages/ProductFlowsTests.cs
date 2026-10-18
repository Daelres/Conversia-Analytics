using System.Net;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>
/// The stale-edit tests lean on <see cref="ProductFlows.ReloadLink"/>. The page shell carries a skip link that, resolved against <c>&lt;base href="/"&gt;</c>,
/// is the very page it sits on plus <c>#main</c>, so on the edit URL it must never be mistaken for the link to the latest version. These tests feed the helper small
/// hand-written pages (no host, no database).
/// </summary>
public class ProductFlowsTests
{
    private static readonly Guid Product = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly string EditPath = ProductFlows.EditUrl(Product);

    private static PageResponse Page(string requestPath, string mainContent, string nav = "")
    {
        var body = $"""
            <html lang="es"><head><base href="/"></head><body>
            <a href="{requestPath}#main">Saltar al contenido</a>
            <header><nav aria-label="Principal"><a href="/products">Productos</a>{nav}</nav></header>
            <main id="main">{mainContent}</main>
            </body></html>
            """;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        return new PageResponse(new Uri(WebTestHost.BaseAddress, requestPath), response, body);
    }

    [Fact]
    public void The_skip_link_alone_is_not_a_reload_link_on_the_edit_url()
        => Assert.Null(ProductFlows.ReloadLink(Page(EditPath, "<p>Modificado por otra persona.</p>"), Product));

    [Fact]
    public void A_real_reload_link_is_found_next_to_the_skip_link_on_the_edit_url()
    {
        var page = Page(EditPath, $"""<p role="alert">Modificado por otra persona. <a href="{EditPath}">Ver la última versión</a></p>""");

        var link = ProductFlows.ReloadLink(page, Product);

        Assert.NotNull(link);
        Assert.Equal(EditPath, link!.GetAttribute("href"));
    }

    [Fact]
    public void A_real_reload_link_is_found_when_the_conflict_is_shown_on_another_url()
    {
        var page = Page(EditPath + "/changed", $"""<p>Modificado. <a href="{EditPath}">Ver la última versión</a></p>""");

        Assert.Equal(EditPath, ProductFlows.ReloadLink(page, Product)!.GetAttribute("href"));
    }

    [Theory]
    [InlineData("""<a href="#main">Saltar</a>""", "a fragment-only link")]
    [InlineData("""<a href="/products/11111111-1111-1111-1111-111111111111/edit#main">Ver</a>""", "the edit path with a fragment")]
    [InlineData("""<a href="/products/22222222-2222-2222-2222-222222222222/edit">Ver</a>""", "the edit page of another product")]
    [InlineData("""<a href="/products">Volver</a>""", "the list")]
    [InlineData("""<div hidden><a href="/products/11111111-1111-1111-1111-111111111111/edit">Ver</a></div>""", "a hidden link")]
    public void A_link_that_is_not_the_edit_page_of_this_product_is_no_reload_link(string linkHtml, string because)
        => Assert.True(ProductFlows.ReloadLink(Page(EditPath, linkHtml), Product) is null, $"Found a reload link in: {because}.");

    [Fact]
    public void A_link_in_the_navigation_is_no_reload_link_even_when_it_points_at_the_edit_page()
        => Assert.Null(ProductFlows.ReloadLink(Page(EditPath, "<p>Modificado.</p>", nav: $"""<a href="{EditPath}">Editar</a>"""), Product));

    [Fact]
    public void A_conflict_page_with_only_the_skip_link_fails_the_conflict_assertion()
    {
        var page = Page(EditPath, $"<p>{SpanishText.ProductModified}</p>");

        Assert.ThrowsAny<Exception>(() => ProductFlows.AssertConflictShown(page, Product));
    }

    [Fact]
    public void A_conflict_page_with_the_message_and_a_real_reload_link_passes_the_conflict_assertion()
    {
        var page = Page(EditPath, $"""<p role="alert">{SpanishText.ProductModified} <a href="{EditPath}">Ver la última versión</a></p>""");

        ProductFlows.AssertConflictShown(page, Product);
    }

    [Fact]
    public void The_manage_links_do_not_count_the_skip_link_on_an_edit_page()
        => Assert.Empty(ProductFlows.ManageLinks(Page(EditPath, "<p>Formulario.</p>")));
}
