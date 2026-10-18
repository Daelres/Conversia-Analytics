using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// A listed product as a person sees it in a table row of <c>/products</c>. Price and stock are read from the raw
/// <c>data-price</c> / <c>data-stock</c> attributes (invariant numbers, never from the displayed Spanish text) and only when
/// asked for, so a test that only compares ids, SKUs or names does not depend on those attributes.
/// </summary>
internal sealed record ProductRowView(Guid Id, string Sku, string Name, string? PriceAttribute, string? StockAttribute)
{
    public ProductRowView(Guid id, string sku, string name, decimal price, int stock)
        : this(id, sku, name, price.ToString("0.00", CultureInfo.InvariantCulture), stock.ToString(CultureInfo.InvariantCulture))
    {
    }

    public decimal Price => decimal.Parse(
        PriceAttribute ?? throw new Xunit.Sdk.XunitException($"The price cell of product {Sku} has no data-price attribute."),
        NumberStyles.Number, CultureInfo.InvariantCulture);

    public int Stock => int.Parse(
        StockAttribute ?? throw new Xunit.Sdk.XunitException($"The stock cell of product {Sku} has no data-stock attribute."),
        NumberStyles.Integer, CultureInfo.InvariantCulture);
}

/// <summary>Browser-level flows of the product pages and the assertions their suites share (see <see cref="PageContract"/> for the markup they rely on).</summary>
internal static class ProductFlows
{
    public const int PageSize = 10;

    public static string ListUrl(int? page = null, string? search = null)
    {
        var parts = new List<string>();
        if (page is not null)
        {
            parts.Add("page=" + page);
        }

        if (search is not null)
        {
            parts.Add("search=" + Uri.EscapeDataString(search));
        }

        return parts.Count == 0 ? "/products" : "/products?" + string.Join("&", parts);
    }

    public static string EditUrl(Guid id) => $"/products/{id}/edit";

    public static string DeleteUrl(Guid id) => $"/products/{id}/delete";

    public static async Task<PageResponse> ListAsync(WebSession session, string url = "/products")
        => (await session.GetAsync(url)).ShouldBe(HttpStatusCode.OK);

    // ---- reading pages ----

    public static IReadOnlyList<ProductRowView> Rows(PageResponse page)
        => page.Select(PageContract.ProductRow).Select(ParseRow).ToList();

    public static ProductRowView Row(PageResponse page, Guid id)
        => Assert.Single(Rows(page), r => r.Id == id);

    private static ProductRowView ParseRow(IElement row)
    {
        string Cell(string field) => row.QuerySelector(PageContract.Field(field))?.TextContent.Trim()
            ?? throw new Xunit.Sdk.XunitException($"A product row has no '{field}' cell: {row.OuterHtml}");

        return new ProductRowView(
            Guid.Parse(row.GetAttribute("data-product-id")!),
            Cell("sku"),
            Cell("name"),
            row.QuerySelector(PageContract.Field("price"))?.GetAttribute("data-price"),
            row.QuerySelector(PageContract.Field("stock"))?.GetAttribute("data-stock"));
    }

    /// <summary>The displayed text of one cell of a product row (<c>sku</c>, <c>name</c>, <c>price</c> or <c>stock</c>), as a person reads it.</summary>
    public static string DisplayedCell(PageResponse page, Guid id, string field)
        => page.Select(PageContract.ProductRow).Single(r => r.GetAttribute("data-product-id") == id.ToString())
            .QuerySelector(PageContract.Field(field))!.TextContent.Trim();

    /// <summary>
    /// A price as a person types it: one separator, a dot or a comma, is the decimal mark (<c>19,99</c> and <c>19.99</c> both mean 19.99).
    /// Only used to read what the pages pre-fill; the pages' own parsing is what the tests exercise.
    /// </summary>
    public static decimal ParsePriceInput(string text)
        => decimal.Parse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    public static bool IsPost(IHtmlFormElement form)
        => string.Equals(form.GetAttribute("method"), "post", StringComparison.OrdinalIgnoreCase);

    private static bool Has(IHtmlFormElement form, string field) => PageContract.ControlNamed(form, field) is not null;

    public static IEnumerable<IHtmlFormElement> CreateForms(PageResponse page)
        => page.Document.Forms.Where(f => IsPost(f) && Has(f, "sku") && !Has(f, "rowVersion") && PageContract.IsVisibleAndUsable(f));

    public static IHtmlFormElement FindCreateForm(PageResponse page)
        => page.FindForm(f => IsPost(f) && Has(f, "sku") && !Has(f, "rowVersion") && PageContract.IsVisibleAndUsable(f), "visible, usable create-product form (POST with a sku field)");

    /// <summary>The edit-product forms a person can use; none on the page of a stale product ("someone else changed it") or one the caller cannot manage.</summary>
    public static IEnumerable<IHtmlFormElement> EditForms(PageResponse page)
        => page.Document.Forms.Where(f => IsPost(f) && Has(f, "rowVersion") && PageContract.IsVisibleAndUsable(f));

    public static IHtmlFormElement FindEditForm(PageResponse page)
        => page.FindForm(f => IsPost(f) && Has(f, "rowVersion") && PageContract.IsVisibleAndUsable(f), "visible, usable edit-product form (POST with a rowVersion field)");

    public static IEnumerable<IHtmlFormElement> OtherUsableForms(PageResponse page)
        => page.Document.Forms.Where(f => !PageContract.IsLogoutForm(f) && PageContract.IsVisibleAndUsable(f)
                                          && !string.Equals(f.GetAttribute("method") ?? "get", "get", StringComparison.OrdinalIgnoreCase));

    public static IHtmlFormElement FindDeleteForm(PageResponse page)
        => page.FindForm(f => IsPost(f) && !PageContract.IsLogoutForm(f) && PageContract.IsVisibleAndUsable(f), "visible, usable delete-confirmation form (POST other than logout)");

    private static readonly Regex ManagePath = new(@"^/products/(?<id>[0-9a-fA-F-]{36})/(?<action>edit|delete)/?$", RegexOptions.Compiled);

    /// <summary>The edit/delete links a person sees (not inside a hidden element), as (product id, action).</summary>
    public static IReadOnlyList<(Guid Id, string Action)> ManageLinks(PageResponse page)
        => ContentLinks(page, mainOnly: false)
            .Select(a => UiStructureAssertions.Resolve(page, a.GetAttribute("href")))
            .Where(target => target is not null)
            .Select(target => ManagePath.Match(target!.AbsolutePath))
            .Where(m => m.Success)
            .Select(m => (Guid.Parse(m.Groups["id"].Value), m.Groups["action"].Value.ToLowerInvariant()))
            .ToList();

    /// <summary>True when the page offers any way to create, edit or delete a product.</summary>
    public static bool OffersManagement(PageResponse page)
        => CreateForms(page).Any() || ManageLinks(page).Count > 0;

    public static string? SearchBoxValue(PageResponse page)
    {
        var form = page.Document.QuerySelector(PageContract.SearchForm) as IHtmlFormElement
            ?? throw new Xunit.Sdk.XunitException($"No search form on {page.RequestUri.PathAndQuery}.");
        return PageContract.ControlNamed(form, "search")?.GetAttribute("value");
    }

    /// <summary>Submits the page's own search form the way a browser would (GET, the form's action, the field named search).</summary>
    public static async Task<PageResponse> SearchAsync(WebSession session, PageResponse page, string term)
    {
        var form = page.Document.QuerySelector(PageContract.SearchForm) as IHtmlFormElement
            ?? throw new Xunit.Sdk.XunitException($"No search form on {page.RequestUri.PathAndQuery}.");
        Assert.True(string.Equals(form.GetAttribute("method") ?? "get", "get", StringComparison.OrdinalIgnoreCase), "The search form must be a GET form (safe, bookmarkable).");
        var control = PageContract.ControlNamed(form, "search") ?? throw new Xunit.Sdk.XunitException("The search form has no search field.");
        var action = form.GetAttribute("action");
        var target = string.IsNullOrEmpty(action) ? page.RequestUri : new Uri(page.RequestUri, action);
        var url = target.AbsolutePath + "?" + Uri.EscapeDataString(control.GetAttribute("name")!) + "=" + Uri.EscapeDataString(term);
        return await session.GetAsync(url);
    }

    // ---- opening and filling forms ----

    public static async Task<(PageResponse Page, FormModel Form)> OpenCreateFormAsync(WebSession session)
    {
        var page = await ListAsync(session);
        return (page, FormModel.From(page, FindCreateForm(page)));
    }

    public static async Task<(PageResponse Page, FormModel Form)> OpenEditFormAsync(WebSession session, Guid id)
    {
        var page = await ListAsync(session, EditUrl(id));
        return (page, FormModel.From(page, FindEditForm(page)));
    }

    public static async Task<(PageResponse Page, FormModel Form)> OpenDeleteFormAsync(WebSession session, Guid id)
    {
        var page = await ListAsync(session, DeleteUrl(id));
        return (page, FormModel.From(page, FindDeleteForm(page)));
    }

    /// <summary>
    /// The forged-target case: the same submission aimed at another product, whether the page carries the id in the
    /// form action, in a field, or both.
    /// </summary>
    public static FormModel Retarget(FormModel form, Guid from, Guid to)
        => form.ReplaceInValues(from.ToString(), to.ToString())
            .WithAction(new Uri(form.ActionUri.ToString().Replace(from.ToString(), to.ToString(), StringComparison.OrdinalIgnoreCase)));

    public static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static FormModel FillCreate(FormModel form, string sku, string name, string price, string stock)
        => form.Set("sku", sku).Set("name", name).Set("price", price).Set("stock", stock);

    public static FormModel FillEdit(FormModel form, string name, string price, string stock)
        => form.Set("name", name).Set("price", price).Set("stock", stock);

    /// <summary>Submits and, like a browser, follows the post/redirect/get hops.</summary>
    public static async Task<PageResponse> SubmitAsync(WebSession session, FormModel form)
        => await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(form));

    public static async Task<PageResponse> CreateAsync(WebSession session, string sku, string name, decimal price, int stock)
    {
        var (_, form) = await OpenCreateFormAsync(session);
        return await SubmitAsync(session, FillCreate(form, sku, name, Invariant(price), stock.ToString(CultureInfo.InvariantCulture)));
    }

    // ---- API traffic ----

    public static IReadOnlyList<ApiCall> ProductCalls(WebTestHost host, int since = 0, HttpMethod? method = null)
        => host.Api.Calls.Skip(since)
            .Where(c => c.Uri.AbsolutePath.StartsWith("/api/products", StringComparison.OrdinalIgnoreCase) && (method is null || c.Method == method))
            .ToList();

    public static IReadOnlyList<ApiCall> WritesSince(WebTestHost host, int since)
        => host.Api.Calls.Skip(since).Where(c => c.Method != HttpMethod.Get).ToList();

    public static NameValueCollection Query(ApiCall call) => HttpUtility.ParseQueryString(call.Uri.Query);

    // ---- shared assertions ----

    public static void AssertSuccessNotice(PageResponse page)
        => Assert.True(page.Select(PageContract.NoticeSuccess).Any(n => !string.IsNullOrWhiteSpace(n.TextContent)),
            $"Expected a visible success confirmation on {page.RequestUri.PathAndQuery} (status {page.Status}). Page text: {page.Text}");

    public static void AssertNoSuccessNotice(PageResponse page)
        => Assert.Empty(page.Select(PageContract.NoticeSuccess));

    public static void AssertLandedOnList(PageResponse page)
    {
        page.ShouldBe(HttpStatusCode.OK);
        Assert.Equal("/products", page.RequestUri.AbsolutePath);
    }

    /// <summary>A refused action: the quoted wording, a status the page may legitimately carry, and no confirmation.</summary>
    public static void AssertNotAuthorized(PageResponse page, params int[] allowedStatuses)
    {
        Assert.Contains(page.Status, allowedStatuses.Length == 0 ? new[] { 200, 403 } : allowedStatuses);
        Assert.Contains(SpanishText.NotAuthorized, page.Text, StringComparison.OrdinalIgnoreCase);
        AssertNoSuccessNotice(page);
        WebFlows.AssertNoStackTrace(page);
    }

    public static void AssertProductNotFound(PageResponse page)
    {
        page.ShouldBe(HttpStatusCode.NotFound);
        Assert.Contains(SpanishText.ProductNotFound, page.Text, StringComparison.OrdinalIgnoreCase);
        AssertNoSuccessNotice(page);
        WebFlows.AssertNoStackTrace(page);
    }

    /// <summary>
    /// The visible anchors that lead somewhere else: no in-page anchors (the skip link, once resolved against the base URL, is the current page
    /// plus a fragment and would pass for any link to it; see <see cref="UiStructureAssertions.IsInPageAnchor"/>), none inside the banner/navigation
    /// landmarks, and with <paramref name="mainOnly"/> only those inside <c>main</c>.
    /// </summary>
    internal static IEnumerable<IElement> ContentLinks(PageResponse page, bool mainOnly)
        => page.Select("a[href]").Where(a => !PageContract.IsHidden(a)
            && !UiStructureAssertions.IsInPageAnchor(page, a)
            && a.Closest("nav, header, [role=navigation], [role=banner]") is null
            && (!mainOnly || a.Closest("main, [role=main]") is not null));

    /// <summary>The link a person follows to see the latest version of a product: a visible anchor in the page's main content to its edit page (no fragment).</summary>
    public static IElement? ReloadLink(PageResponse page, Guid productId)
        => ContentLinks(page, mainOnly: true).FirstOrDefault(a =>
        {
            var target = UiStructureAssertions.Resolve(page, a.GetAttribute("href"));
            return target is not null && target.Fragment.Length == 0 && target.AbsolutePath == EditUrl(productId);
        });

    /// <summary>A save that lost against another save: the quoted wording, a link to the latest version, no confirmation.</summary>
    public static void AssertConflictShown(PageResponse page, Guid productId)
    {
        Assert.Contains(page.Status, new[] { 200, 409 });
        Assert.Contains(SpanishText.ProductModified, page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ReloadLink(page, productId));
        AssertNoSuccessNotice(page);
        WebFlows.AssertNoStackTrace(page);
    }

    public static void AssertUnavailable(PageResponse page)
    {
        Assert.Contains(page.Status, new[] { 200, 503 });
        Assert.Contains(SpanishText.Unavailable, page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SpanishText.ProductNotFound, page.Text, StringComparison.OrdinalIgnoreCase);
        AssertNoSuccessNotice(page);
        WebFlows.AssertNoStackTrace(page);
    }

    /// <summary>
    /// A rejected product form must show a visible reason. When the Web forwarded the submission to the API (a call of the
    /// given verb and path since <paramref name="callsBefore"/>), the reason is what the API itself answers for the same body.
    /// When it rejected the input locally and made no such call (client-side DataAnnotations are legitimate), any visible
    /// message will do: the page text must differ from the pristine page.
    /// </summary>
    public static async Task AssertRejectionShownAsync(
        TestApiFactory api, WebTestHost host, WebSession session, PageResponse response, int callsBefore,
        string pristineUrl, string ownerToken, HttpMethod method, string apiPath, object body)
    {
        var forwarded = host.Api.Calls.Skip(callsBefore).Any(c => c.Method == method && c.Uri.AbsolutePath == apiPath);
        if (!forwarded)
        {
            var pristine = (await session.GetAsync(pristineUrl)).ShouldBe(HttpStatusCode.OK);
            Assert.NotEqual(pristine.Text, response.Text);
            return;
        }

        var messages = await ApiProducts.ValidationMessagesAsync(api, ownerToken, method, apiPath, body);
        Assert.NotEmpty(messages);
        Assert.True(
            messages.Any(m => response.Text.Contains(m, StringComparison.Ordinal)),
            $"The page shows none of the API's validation messages ({string.Join(" | ", messages)}). Page text: {response.Text}");
    }
}
