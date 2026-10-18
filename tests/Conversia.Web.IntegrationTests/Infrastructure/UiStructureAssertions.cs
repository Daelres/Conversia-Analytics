using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Conversia.TestSupport;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>The kinds of page of the Web, named by the <c>data-page</c> hook their main content region carries.</summary>
internal enum UiPageType
{
    Login,
    Register,
    Home,
    Demo,
    Users,
    Products,
    ProductEdit,
    ProductDelete,
    NotFound,
    Error,
    Forbidden,
}

/// <summary>
/// The page-type table of the structure rules (phase "ui-structure"). A rule that does not apply to a page type is encoded here, not skipped silently.
/// <code>
/// type           data-page       route(s)                         app shell(*)  primary nav   active nav entry     description
/// Login          login           /login                           no            optional      -                    yes (anonymous)
/// Register       register        /register                        no            optional      -                    yes (anonymous)
/// Home           home            /                                yes           required      "/"                  yes (anonymous)
/// Demo           demo            /demo                            yes           required      "/demo"              yes (anonymous)
/// Users          users           /users                           yes           required      "/users"             -
/// Products       products        /products                        yes           required      "/products"          -
/// ProductEdit    product-edit    /products/{id}/edit              yes           required      "/products"          -
/// ProductDelete  product-delete  /products/{id}/delete            yes           required      "/products"          -
/// Forbidden      forbidden       any page answering 403 (/users,  yes           required      route of the request -
///                                /products, edit and delete pages)
/// NotFound       not-found       any 404 (also unknown product)   yes           required      not constrained      -
/// Error          error           /Error (unhandled exception) and yes           required      not constrained      -
///                                any HTML 400 (antiforgery failure)
/// </code>
/// The login page also covers its 429 state (the API throttled the sign-in: the page answers 200 and re-renders the form with an alert).
/// (*) The shell is the header (banner), the footer (contentinfo) and the primary navigation. The auth pages are split-screen
/// pages without a shell: a primary navigation is allowed there, but when one exists it must follow the same rules. Everything
/// else (document, skip link, main, h1, theme toggle, forms, scripts, styles) applies to every page type.
/// A page answering 403 is <c>forbidden</c> whatever its route; a page answering 404 is <c>not-found</c>; an HTML page answering 400
/// (a rejected antiforgery token) is an <c>error</c> page about an invalid or expired request and never says the page was not found.
/// Today that answer is an EMPTY body that still carries <c>Content-Type: text/html</c> (so the status-code re-execution does not fire): it is NOT yet
/// a page, and satisfying this requirement needs a new production handler that renders the error page for it.
/// Plain-text 400 bodies (logout and permission endpoints) are not pages and are not part of the matrix.
/// </summary>
internal static class UiPageTypes
{
    public static string DataPage(UiPageType type) => type switch
    {
        UiPageType.Login => "login",
        UiPageType.Register => "register",
        UiPageType.Home => "home",
        UiPageType.Demo => "demo",
        UiPageType.Users => "users",
        UiPageType.Products => "products",
        UiPageType.ProductEdit => "product-edit",
        UiPageType.ProductDelete => "product-delete",
        UiPageType.NotFound => "not-found",
        UiPageType.Error => "error",
        UiPageType.Forbidden => "forbidden",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static readonly IReadOnlySet<string> AllDataPageNames = Enum.GetValues<UiPageType>().Select(DataPage).ToHashSet();

    public static bool HasShell(UiPageType type) => type is not (UiPageType.Login or UiPageType.Register);

    public static bool NeedsDescription(UiPageType type) => type is UiPageType.Login or UiPageType.Register or UiPageType.Home or UiPageType.Demo;
}

/// <summary>One fetched page of the matrix, plus what a person (or the requirement) expects of that state.</summary>
internal sealed class UiPageCase
{
    public required string Label { get; init; }

    public required PageResponse Page { get; init; }

    public required UiPageType Type { get; init; }

    public bool Authenticated { get; init; }

    /// <summary>The page is the answer to a form post (or was reached through one): a rejection or a confirmation is on it.</summary>
    public bool Submitted { get; init; }

    /// <summary>A transient error message (not a field error) must be announced: <c>role="alert"</c>.</summary>
    public bool ExpectAlert { get; init; }

    /// <summary>A confirmation must be announced politely: <c>role="status"</c>, and no error alert may be on the page.</summary>
    public bool ExpectStatus { get; init; }

    /// <summary>Last name segments of the form fields that were rejected and must expose their error (<c>aria-invalid</c> + <c>aria-describedby</c>).</summary>
    public string[] InvalidFields { get; init; } = [];

    public IHtmlDocument Document => Page.Document;

    public override string ToString() => Label;

    /// <summary>The path of the navigation entry that must be marked <c>aria-current="page"</c>; null when the page type does not constrain it.</summary>
    public string? ExpectedActiveNavPath => Type switch
    {
        UiPageType.Home => "/",
        UiPageType.Demo => "/demo",
        UiPageType.Users => "/users",
        UiPageType.Products or UiPageType.ProductEdit or UiPageType.ProductDelete => "/products",
        UiPageType.Forbidden when Page.RequestUri.AbsolutePath.StartsWith("/users", StringComparison.OrdinalIgnoreCase) => "/users",
        UiPageType.Forbidden when Page.RequestUri.AbsolutePath.StartsWith("/products", StringComparison.OrdinalIgnoreCase) => "/products",
        _ => null,
    };
}

/// <summary>One fetched file (stylesheet or icon): the buffered response plus the exact bytes served.</summary>
internal sealed class UiAsset(PageResponse response, byte[] bytes)
{
    public PageResponse Response { get; } = response;

    public byte[] Bytes { get; } = bytes;

    public int Status => Response.Status;

    /// <summary>The body decoded as UTF-8 (meaningful for text files).</summary>
    public string Text => Response.Body;

    public string? Header(string name) => Response.Header(name);

    /// <summary>The Content-Type without parameters, lower-cased.</summary>
    public string? MediaType => Header("Content-Type")?.Split(';')[0].Trim().ToLowerInvariant();
}

/// <summary>The files the pages link (stylesheets, icons and the sheets they import), fetched once through the same host: absolute URL to file.</summary>
internal sealed class UiAssets(IReadOnlyDictionary<Uri, UiAsset> responses)
{
    private readonly ConcurrentDictionary<PageResponse, string> _css = new();

    public IReadOnlyDictionary<Uri, UiAsset> Responses { get; } = responses;

    public UiAsset? Find(Uri url) => Responses.TryGetValue(url, out var response) ? response : null;

    /// <summary>The CSS that applies to one page: its linked sheets, the same-origin sheets they import, and its inline style blocks (cached per page).</summary>
    public string CssOf(PageResponse page) => _css.GetOrAdd(page, p => UiStructureAssertions.PageCss(p, this));
}

/// <summary>The favicon.png the template ships (src/Conversia.Web/wwwroot/favicon.png at scaffolding), embedded so the check does not depend on the working tree.</summary>
internal static class DefaultFavicon
{
    private const string Base64 = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAEQ0lEQVR4XsVWTWxMURQ+XgaqagiFRkwjQcRCoxIrdKkJDFY0RRdUbEotREOMaFiAsWtZgEAkpEGiK6YsqZBIRRFGiqCio+0U8Jzv5n7JzctkzDTwJSd59+fdc853fu4d4vu+/E+E5B9hYGCgVAcnVXpU4gUFBQnMe39JWVQlprLFmY6rLFJZrnKTawjBn1a+XgcnnKkalTEqRzBo2vNeNu0uxmdKpdT7kxSrtFD53UQ/l2BQDINLxz7K7Wu98qh9AMOwSjSUR/wgFZIZZZZaSff+lNOHP0j3628yr6JQLO2S7Pwql49/FKDtWp/MKi+QbAZAKWjbYj2ISA64pd7By+4332XlhrECUHlj7WshwAANz2gAEsTSFqZXyc4v5qB+/SZcBTg03cc1kYVLiwRoPZ+CUe6aMdAiEgp6bbN1HS29fv6TtLf1Sz6YNXeE+bfRshEEnRpZ5LEKhAYkEDMsIltdxSNHeRKZMcz1ml4NCvvOTjHnhRzlcSrfq/F6+eSrAOWLCqVy9WgmDcFYGnof3fss+YLOeFY5srvOVQ6Ptx6YKPUHJ8r4kqFy5tAHJBLFjIGdzSXYh/2Db8W2RWq8U1DOg2GlUdR6ISVBwGvMgyE0FuxvqOrKOUcs2jzrfeS9JgvrtLp+HJTDU6MkG5AnYK14csj8lwPccN73bJ3Lrau9AozXgxavDjO2nDM0n70zjSLHb5TK1OkmjmAN+/Gf2fsbsEEBCY/dja2zUpUjF1C/VL4fGTtzuFFCuaue71LaqRBMgUX8/xv6mYApvRFb8HeEXpCeDqeprNo4Vvr1u2FNF+d4kCxcUmSoZ623qxM4PBvAkpt3HpuCWx6ocQJKEJ4cah5dEg5k8570g944DTDU/W2gTGtjEzjEg+QFDSBtQmOKS0ICMDfQ14N1Tk/RIwj8RzaDqNVSRbgUD1R5jPOeayG7Gw/nHV6oa6hz1DxoxGHIDYRqrZZetRWEC70kk3JSz6ojJJ1OJ1T8xs3P/RWzH/oN1U99jA9vT2JspC7a6SeffcK8ke63fWa9an6H33rxLef8M/FX2E9x1yE9KmW4e1wZopO4eo/gLm/WCwjY2TRZwzJcdlR1McNZkmCKFZMV6BGbNOYsOZS7Un9fCCcELcx2KOC7Dag/OAkHufd4VuU0EpTvPzeFMQd6qDwIz2bjKRX0dCpie0WzwesGB2dVukAdQLc8emWqzNNcwR2CZmUxxrw1guCr2C7CkDBDwcSs3maSixWCt17GauAbAUmIhsT/LFag60kQrAJd7LEtOQVloBBA84ExdcteGo+Sj78I4VZJk92jtyH2oGKgnLGvofJMCL6IorZFhuFtc+wdL6Sckq5yTdj1OqkSZexzMYBGlFkj5pB2UNrR/lmbzA8h2IRKNcvLKwqZcMQelbhlVvIxIPgyhkQkNyRhOIRtFhisAQSfa5QgoAgUJ4JU52XA/8QvRgye1rf4g4QAAAAASUVORK5CYII=";

    public static readonly byte[] Bytes = Convert.FromBase64String(Base64);

    private static readonly byte[] Hash = SHA256.HashData(Bytes);

    /// <summary>True when the served bytes are the template's default favicon (same length and SHA-256), whatever the file is called.</summary>
    public static bool Matches(byte[] served) => served.Length == Bytes.Length && SHA256.HashData(served).AsSpan().SequenceEqual(Hash);
}

/// <summary>
/// The structural and accessibility quality bar of the redesigned Web (phase "ui-structure"), one method per rule group. Each returns
/// the problems it found (empty = compliant) so a matrix test can report every page and rule at once. Markup-structural on purpose:
/// no class names are pinned (the only styling hook is <c>data-align="end"</c>); the <c>data-*</c> hooks are listed in each rule.
/// </summary>
internal static class UiStructureAssertions
{
    private const string FocusableSelector =
        "a[href], area[href], button, input:not([type=hidden]), select, textarea, summary, [tabindex], iframe, [contenteditable=true]";

    private static readonly string[] InputTypesWithoutLabel = ["hidden", "submit", "button", "image", "reset"];

    // ---------------------------------------------------------------- reading helpers

    public static bool IsHidden(IElement element) => element.Closest("[hidden]") is not null;

    public static IEnumerable<IElement> Visible(IEnumerable<IElement> elements) => elements.Where(e => !IsHidden(e));

    private static string Normalize(string? text) => Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

    /// <summary>The base the page's relative URLs resolve against: the request URL, or its <c>&lt;base href&gt;</c>.</summary>
    public static Uri BaseUri(PageResponse page)
    {
        var baseHref = page.Document.QuerySelector("base[href]")?.GetAttribute("href");
        return baseHref is null ? page.RequestUri : new Uri(page.RequestUri, baseHref);
    }

    public static Uri? Resolve(PageResponse page, string? href)
        => !string.IsNullOrWhiteSpace(href) && Uri.TryCreate(BaseUri(page), href.Trim(), out var uri) ? uri : null;

    /// <summary>True when <paramref name="uri"/> is on the same scheme, host and port as the page being viewed.</summary>
    public static bool IsSameOriginAs(PageResponse page, Uri uri)
        => string.Equals(uri.GetLeftPart(UriPartial.Authority), page.RequestUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    /// <summary>The fragment of a URL without its <c>#</c>, unescaped (what an element <c>id</c> is compared with).</summary>
    public static string FragmentOf(Uri uri) => Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

    /// <summary>
    /// A link a person (or the page's author) means as "jump inside this page": a fragment-only href (<c>#main</c>), a same-origin href that
    /// resolves to this very page plus a fragment, or one whose fragment names an element of this page (<c>/#main</c> on <c>/register</c> is a
    /// botched attempt at it). A fragment-only href is recognised as authored, never by what it resolves to: resolving a bare <c>#main</c>
    /// against <c>&lt;base href="/"&gt;</c> is exactly the defect.
    /// </summary>
    public static bool IsInPageAnchor(PageResponse page, IElement link)
    {
        var href = link.GetAttribute("href")?.Trim();
        if (string.IsNullOrEmpty(href) || !href.Contains('#'))
        {
            return false;
        }

        if (href.StartsWith('#'))
        {
            return true;
        }

        return Resolve(page, href) is { } target
            && IsSameOriginAs(page, target)
            && (target.PathAndQuery == page.RequestUri.PathAndQuery
                || (FragmentOf(target) is { Length: > 0 } fragment && page.Document.GetElementById(fragment) is not null));
    }

    /// <summary>The URL an in-page link must resolve to: the page being viewed (path and query) plus <paramref name="fragment"/>.</summary>
    public static string InPageUrl(PageResponse page, string fragment) => page.RequestUri.PathAndQuery + "#" + fragment;

    private static bool IsSameOrigin(Uri uri) => uri.Scheme == WebTestHost.BaseAddress.Scheme && uri.Host == WebTestHost.BaseAddress.Host && uri.Port == WebTestHost.BaseAddress.Port;

    /// <summary>Every same-origin stylesheet and icon the page links, and what its inline styles import (what <see cref="UiAssets"/> must hold; the imports of linked sheets are followed by the fetcher).</summary>
    public static IEnumerable<Uri> LinkedFiles(PageResponse page)
        => page.Select("link[rel~=stylesheet][href], link[rel~=icon][href]").Select(e => Resolve(page, e.GetAttribute("href")))
            .Where(u => u is not null && IsSameOrigin(u)).Select(u => u!)
            .Concat(page.Select("style").SelectMany(style => CssImports(style.TextContent, BaseUri(page))));

    private static IEnumerable<Uri> StylesheetUrls(PageResponse page)
        => page.Select("link[rel~=stylesheet][href]").Select(e => Resolve(page, e.GetAttribute("href"))).Where(u => u is not null).Select(u => u!);

    private static string StripCssComments(string css) => Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    /// <summary>The same-origin sheets an <c>@import</c> rule of <paramref name="css"/> (the text of <paramref name="source"/>) asks for.</summary>
    public static IEnumerable<Uri> CssImports(string css, Uri source)
    {
        const string pattern = "@import\\s+(?:url\\(\\s*(?:\"(?<a>[^\"]+)\"|'(?<b>[^']+)'|(?<c>[^)\\s\"']+))\\s*\\)|\"(?<d>[^\"]+)\"|'(?<e>[^']+)')";
        foreach (Match match in Regex.Matches(StripCssComments(css), pattern, RegexOptions.IgnoreCase))
        {
            var href = new[] { "a", "b", "c", "d", "e" }.Select(g => match.Groups[g]).First(g => g.Success).Value.Trim();
            if (Uri.TryCreate(source, href, out var uri) && IsSameOrigin(uri))
            {
                yield return uri;
            }
        }
    }

    /// <summary>Every same-origin sheet that applies to the page (linked ones, inline-style imports, and what they import), imports before the sheet that imports them.</summary>
    private static List<(Uri Url, UiAsset? Asset, Uri? ImportedFrom)> SheetTree(PageResponse page, UiAssets assets)
    {
        var seen = new HashSet<Uri>();
        var result = new List<(Uri, UiAsset?, Uri?)>();

        void Visit(Uri url, Uri? importedFrom, int depth)
        {
            if (!IsSameOrigin(url) || depth > 8 || !seen.Add(url))
            {
                return;
            }

            var asset = assets.Find(url);
            if (asset is { Status: 200 })
            {
                foreach (var import in CssImports(asset.Text, url))
                {
                    Visit(import, url, depth + 1);
                }
            }

            result.Add((url, asset, importedFrom));
        }

        foreach (var sheet in StylesheetUrls(page))
        {
            Visit(sheet, null, 0);
        }

        foreach (var style in page.Select("style"))
        {
            foreach (var import in CssImports(style.TextContent, BaseUri(page)))
            {
                Visit(import, page.RequestUri, 1);
            }
        }

        return result;
    }

    /// <summary>The CSS that applies to one page: the sheets it links with the ones they import, followed by its inline <c>&lt;style&gt;</c> blocks.</summary>
    public static string PageCss(PageResponse page, UiAssets assets)
        => string.Join("\n", SheetTree(page, assets).Where(t => t.Asset is { Status: 200 }).Select(t => t.Asset!.Text)
            .Concat(page.Select("style").Select(style => style.TextContent)));

    /// <summary>The accessible name of an element, simplified from the accname algorithm to what these pages can contain.</summary>
    public static string AccessibleName(IElement element)
    {
        var document = element.Owner!;

        var labelledBy = element.GetAttribute("aria-labelledby");
        if (!string.IsNullOrWhiteSpace(labelledBy))
        {
            var text = Normalize(string.Join(' ', labelledBy.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => document.GetElementById(id)).Where(e => e is not null).Select(e => e!.TextContent)));
            if (text.Length > 0)
            {
                return text;
            }
        }

        if (Normalize(element.GetAttribute("aria-label")) is { Length: > 0 } ariaLabel)
        {
            return ariaLabel;
        }

        if (element.LocalName is "input" or "select" or "textarea")
        {
            var type = element.GetAttribute("type")?.ToLowerInvariant();
            if (type is "submit" or "button" or "reset" && Normalize(element.GetAttribute("value")) is { Length: > 0 } value)
            {
                return value;
            }

            if (type == "image" && Normalize(element.GetAttribute("alt")) is { Length: > 0 } alt)
            {
                return alt;
            }

            var id = element.GetAttribute("id");
            if (!string.IsNullOrEmpty(id))
            {
                var label = document.QuerySelectorAll("label[for]").FirstOrDefault(l => l.GetAttribute("for") == id && NameFromContent(l).Length > 0);
                if (label is not null)
                {
                    return NameFromContent(label);
                }
            }

            if (element.Closest("label") is { } wrapping && NameFromContent(wrapping) is { Length: > 0 } wrapped)
            {
                return wrapped;
            }
        }
        else if (NameFromContent(element) is { Length: > 0 } content)
        {
            return content;
        }

        return Normalize(element.GetAttribute("title"));
    }

    private static string NameFromContent(IElement element)
    {
        var builder = new StringBuilder();
        foreach (var child in element.ChildNodes)
        {
            Collect(child, builder);
        }

        return Normalize(builder.ToString());
    }

    private static void Collect(INode node, StringBuilder builder)
    {
        if (node is IElement element)
        {
            if (element.HasAttribute("hidden") || element.GetAttribute("aria-hidden") == "true" || element.LocalName is "script" or "style")
            {
                return;
            }

            if (element.LocalName == "img")
            {
                builder.Append(' ').Append(element.GetAttribute("alt")).Append(' ');
                return;
            }

            if (element.LocalName == "svg")
            {
                builder.Append(' ').Append(SvgName(element)).Append(' ');
                return;
            }

            if (Normalize(element.GetAttribute("aria-label")) is { Length: > 0 } label)
            {
                builder.Append(' ').Append(label).Append(' ');
                return;
            }
        }
        else if (node.NodeType == NodeType.Text)
        {
            builder.Append(node.TextContent);
            return;
        }

        foreach (var child in node.ChildNodes)
        {
            Collect(child, builder);
        }
    }

    private static string SvgName(IElement svg)
    {
        if (Normalize(svg.GetAttribute("aria-label")) is { Length: > 0 } label)
        {
            return label;
        }

        var labelledBy = svg.GetAttribute("aria-labelledby");
        if (!string.IsNullOrWhiteSpace(labelledBy))
        {
            var text = Normalize(string.Join(' ', labelledBy.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => svg.Owner!.GetElementById(id)).Where(e => e is not null).Select(e => e!.TextContent)));
            if (text.Length > 0)
            {
                return text;
            }
        }

        return Normalize(svg.QuerySelector("title")?.TextContent);
    }

    /// <summary>The primary navigation: the one <c>nav</c> that links to <c>/demo</c> (the tour is linked from the navigation of every page that has one).</summary>
    public static IElement? PrimaryNav(PageResponse page)
        => page.Select("nav").FirstOrDefault(n => n.QuerySelectorAll("a[href]").Any(a => Resolve(page, a.GetAttribute("href"))?.AbsolutePath == "/demo"));

    private static IEnumerable<IElement> Focusable(PageResponse page)
        => page.Document.Body!.QuerySelectorAll(FocusableSelector)
            .Where(e => !IsHidden(e) && !e.HasAttribute("disabled"))
            .Where(e => !e.HasAttribute("tabindex") || int.TryParse(e.GetAttribute("tabindex"), out var index) && index >= 0);

    private static IEnumerable<string> Prefixed(UiPageCase page, IEnumerable<string> problems) => problems.Select(p => $"[{page.Label}] {p}");

    // ---------------------------------------------------------------- 1. document

    /// <summary>lang, charset, viewport, title, color-scheme, description (public pages) and a non-default favicon that resolves.</summary>
    public static IEnumerable<string> Document(UiPageCase page, UiAssets assets)
    {
        var document = page.Document;
        var problems = new List<string>();

        var html = document.DocumentElement;
        if (!string.Equals(html.GetAttribute("lang"), "es", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"<html lang> must be \"es\" but is \"{html.GetAttribute("lang")}\".");
        }

        if (document.QuerySelector("meta[charset]") is null)
        {
            problems.Add("There is no <meta charset>.");
        }

        problems.AddRange(Viewport(document));

        if (string.IsNullOrWhiteSpace(document.Title))
        {
            problems.Add("The <title> is empty.");
        }

        var colorScheme = Normalize(document.QuerySelector("meta[name='color-scheme']")?.GetAttribute("content")).ToLowerInvariant();
        var colorSchemeOk = colorScheme.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet() is var schemes && schemes.Contains("light") && schemes.Contains("dark");
        var themeColors = document.QuerySelectorAll("meta[name='theme-color']").Select(m => Normalize(m.GetAttribute("media")).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant()).ToList();
        var themeColorOk = themeColors.Any(m => m.Contains("prefers-color-scheme:light", StringComparison.Ordinal)) && themeColors.Any(m => m.Contains("prefers-color-scheme:dark", StringComparison.Ordinal));
        if (!colorSchemeOk && !themeColorOk)
        {
            problems.Add("Neither <meta name=\"color-scheme\" content=\"light dark\"> nor a light and a dark theme-color (with prefers-color-scheme media) exist.");
        }

        if (UiPageTypes.NeedsDescription(page.Type) && !page.Authenticated
            && string.IsNullOrWhiteSpace(document.QuerySelector("meta[name='description']")?.GetAttribute("content")))
        {
            problems.Add("A public page needs a non-empty <meta name=\"description\">.");
        }

        problems.AddRange(Favicon(page.Page, assets));
        return Prefixed(page, problems);
    }

    private static IEnumerable<string> Viewport(IHtmlDocument document)
    {
        var content = Normalize(document.QuerySelector("meta[name='viewport']")?.GetAttribute("content")).ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (content.Length == 0)
        {
            yield return "There is no <meta name=\"viewport\">.";
            yield break;
        }

        var settings = content.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        if (!settings.TryGetValue("width", out var width) || width != "device-width")
        {
            yield return $"The viewport must set width=device-width (it is \"{content}\").";
        }

        if (!settings.TryGetValue("initial-scale", out var scale) || !double.TryParse(scale, System.Globalization.CultureInfo.InvariantCulture, out var number) || number != 1)
        {
            yield return $"The viewport must set initial-scale=1 (it is \"{content}\").";
        }

        if (settings.TryGetValue("user-scalable", out var scalable) && scalable is "no" or "0")
        {
            yield return "The viewport must not disable zoom (user-scalable).";
        }

        if (settings.ContainsKey("maximum-scale"))
        {
            yield return "The viewport must not cap zoom (maximum-scale).";
        }
    }

    private static IEnumerable<string> Favicon(PageResponse page, UiAssets assets)
    {
        var hrefs = page.Select("link[rel~=icon][href]").Select(e => e.GetAttribute("href")!.Trim()).ToList();
        if (hrefs.Count == 0)
        {
            yield return "There is no <link rel=\"icon\">.";
            yield break;
        }

        foreach (var href in hrefs)
        {
            if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Regex.IsMatch(href, @"^data:\s*image/[\w.+-]+", RegexOptions.IgnoreCase))
                {
                    yield return $"The data: icon is not an image/* ({href[..Math.Min(href.Length, 40)]}).";
                }

                continue;
            }

            if (Resolve(page, href) is not { } icon)
            {
                yield return $"The icon href \"{href}\" is not a valid URL.";
                continue;
            }

            var asset = assets.Find(icon);
            if (asset is null || asset.Status != 200)
            {
                yield return $"The icon {icon.AbsolutePath} does not resolve with 200 (got {asset?.Status.ToString() ?? "no response"}).";
            }
            else if (asset.MediaType?.StartsWith("image/", StringComparison.Ordinal) != true)
            {
                yield return $"The icon {icon.AbsolutePath} is served as \"{asset.Header("Content-Type")}\", not image/*.";
            }
            else if (DefaultFavicon.Matches(asset.Bytes))
            {
                yield return $"The icon {icon.AbsolutePath} serves the bytes of the default favicon.png of the template (a renamed copy is still the default icon).";
            }
        }
    }

    // ---------------------------------------------------------------- 2. landmarks, skip link, headings, navigation

    /// <summary>One main with an id, the skip link first, banner and contentinfo on shell pages, a labelled primary navigation.</summary>
    public static IEnumerable<string> Landmarks(UiPageCase page)
    {
        var document = page.Document;
        var problems = new List<string>();

        var mains = document.QuerySelectorAll("main, [role=main]").ToList();
        var main = mains.FirstOrDefault();
        if (mains.Count != 1)
        {
            problems.Add($"Expected exactly one <main>, found {mains.Count}.");
        }
        else if (string.IsNullOrWhiteSpace(main!.Id))
        {
            problems.Add("The <main> has no id (the skip link targets it).");
        }

        var first = document.Body is null ? null : Focusable(page.Page).FirstOrDefault();
        if (first is null || first.LocalName != "a")
        {
            problems.Add($"The first focusable element of the body must be the skip link, but it is {Describe(first)}.");
        }
        else
        {
            var href = first.GetAttribute("href");
            if (main is null || string.IsNullOrWhiteSpace(main.Id))
            {
                problems.Add($"The first focusable link must point to the main landmark but the <main> has no id; its href is \"{href}\".");
            }
            else if (InPageLinkProblem(page.Page, href, expectedFragment: main.Id) is { } problem)
            {
                problems.Add($"The first focusable link must point to the main landmark: {problem}");
            }

            if (!AccessibleName(first).Contains("Saltar", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"The skip link text must contain \"Saltar\" but is \"{AccessibleName(first)}\".");
            }
        }

        if (UiPageTypes.HasShell(page.Type))
        {
            if (!document.QuerySelectorAll("header, [role=banner]").Any(h => h.LocalName != "header" || h.Closest("main, article, section, aside, nav") is null))
            {
                problems.Add("The app shell has no banner (a <header> outside main/article/section, or role=banner).");
            }

            if (!document.QuerySelectorAll("footer, [role=contentinfo]").Any(f => f.LocalName != "footer" || f.Closest("main, article, section, aside, nav") is null))
            {
                problems.Add("The app shell has no contentinfo (a <footer> outside main/article/section, or role=contentinfo).");
            }

            if (PrimaryNav(page.Page) is null)
            {
                problems.Add("The app shell has no primary <nav> (the navigation that links to /demo).");
            }
        }

        var navs = document.QuerySelectorAll("nav").Where(n => !IsHidden(n)).ToList();
        foreach (var nav in navs.Where(n => string.IsNullOrWhiteSpace(n.GetAttribute("aria-label")) && string.IsNullOrWhiteSpace(n.GetAttribute("aria-labelledby"))))
        {
            problems.Add($"A <nav> has no aria-label: {Snippet(nav)}");
        }

        var labels = navs.Select(n => Normalize(n.GetAttribute("aria-label"))).Where(l => l.Length > 0).ToList();
        if (labels.Count != labels.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            problems.Add($"The <nav> landmarks must have distinct labels: {string.Join(" | ", labels)}.");
        }

        return Prefixed(page, problems);
    }

    /// <summary>
    /// What is wrong with an in-page link, or null when it is fine: once resolved against the document's base URL (<c>&lt;base href&gt;</c>, else the page URL)
    /// it must be the very page being viewed (same path and query) plus a fragment. A bare <c>#main</c> under <c>&lt;base href="/"&gt;</c> resolves to
    /// <c>/#main</c> and takes the person to the home page. With <paramref name="expectedFragment"/> the fragment must be that one.
    /// </summary>
    public static string? InPageLinkProblem(PageResponse page, string? href, string? expectedFragment = null)
    {
        var target = Resolve(page, href);
        if (target is null)
        {
            return $"its href \"{href}\" is not a valid URL.";
        }

        var expected = InPageUrl(page, expectedFragment ?? FragmentOf(target));
        var resolved = target.PathAndQuery + target.Fragment;
        if (!IsSameOriginAs(page, target) || target.PathAndQuery != page.RequestUri.PathAndQuery)
        {
            return $"its href \"{href}\" resolves (against {BaseUri(page).AbsoluteUri}) to {target.AbsoluteUri}, which leaves the page: an in-page link must resolve to {expected}.";
        }

        var fragment = FragmentOf(target);
        if (fragment.Length == 0)
        {
            return $"its href \"{href}\" has an empty fragment: it must resolve to {expected}.";
        }

        if (expectedFragment is not null && fragment != expectedFragment)
        {
            return $"its href \"{href}\" resolves to {resolved} but must resolve to {expected}.";
        }

        return page.Document.GetElementById(fragment) is null
            ? $"its href \"{href}\" targets #{fragment}, but no element of the page has that id."
            : null;
    }

    /// <summary>
    /// Every link that jumps inside the page (see <see cref="IsInPageAnchor"/>: the skip link, the create-panel buttons, an empty-state call to action)
    /// resolves, against the document's base URL, to the page being viewed (path and query) plus a fragment naming an element of the page.
    /// Links to other pages or sites that merely carry a fragment are not in-page links and are not judged.
    /// </summary>
    public static IEnumerable<string> InPageLinks(UiPageCase page)
        => Prefixed(page, page.Document.QuerySelectorAll("a[href], area[href]")
            .Where(link => IsInPageAnchor(page.Page, link))
            .Select(link => (Link: link, Problem: InPageLinkProblem(page.Page, link.GetAttribute("href"))))
            .Where(x => x.Problem is not null)
            .Select(x => $"In-page link {Snippet(x.Link)}: {x.Problem}"));

    /// <summary>
    /// Exactly one visible h1, and after the first heading no jump of more than one level (h2 then h4). A heading that precedes the h1 in
    /// DOM order (a sidebar title before <c>main</c>) is fine: going back up never skips anything.
    /// </summary>
    public static IEnumerable<string> Headings(UiPageCase page)
    {
        var problems = new List<string>();
        var headings = Visible(page.Document.QuerySelectorAll("h1, h2, h3, h4, h5, h6, [role=heading][aria-level]")).ToList();

        var h1s = headings.Where(h => Level(h) == 1).ToList();
        if (h1s.Count != 1)
        {
            problems.Add($"Expected exactly one <h1>, found {h1s.Count}.");
        }
        else if (string.IsNullOrWhiteSpace(h1s[0].TextContent))
        {
            problems.Add("The <h1> is empty.");
        }

        foreach (var (previous, heading) in headings.Zip(headings.Skip(1), (before, after) => (Level(before), after)))
        {
            var level = Level(heading);
            if (level > previous + 1)
            {
                problems.Add($"Heading level skipped: h{previous} is followed by h{level} \"{Normalize(heading.TextContent)}\".");
            }
        }

        return Prefixed(page, problems);
    }

    private static int Level(IElement heading)
        => heading.LocalName.Length == 2 && heading.LocalName[0] == 'h' && char.IsDigit(heading.LocalName[1])
            ? heading.LocalName[1] - '0'
            : int.TryParse(heading.GetAttribute("aria-level"), out var level) ? level : 2;

    /// <summary>Exactly one entry of the primary navigation is the current page where the page type fixes it, and never more than one.</summary>
    public static IEnumerable<string> Navigation(UiPageCase page)
    {
        var problems = new List<string>();
        var nav = PrimaryNav(page.Page);
        if (nav is null)
        {
            // Without a primary navigation only the auth pages are valid (Landmarks reports the missing one on shell pages).
            return Prefixed(page, UiPageTypes.HasShell(page.Type) ? new[] { "There is no primary navigation to carry the current-page marker." } : Array.Empty<string>());
        }

        var current = nav.QuerySelectorAll("[aria-current]").Where(e => e.GetAttribute("aria-current") == "page").ToList();
        if (nav.QuerySelectorAll("[aria-current]").Any(e => e.GetAttribute("aria-current") != "page"))
        {
            problems.Add("aria-current in the navigation must be exactly \"page\".");
        }

        var expected = page.ExpectedActiveNavPath;
        if (expected is not null)
        {
            if (current.Count != 1)
            {
                problems.Add($"Expected exactly one navigation entry with aria-current=\"page\" (for {expected}), found {current.Count}.");
            }
            else if (Resolve(page.Page, current[0].GetAttribute("href"))?.AbsolutePath != expected)
            {
                problems.Add($"The current navigation entry must link to {expected} but links to \"{current[0].GetAttribute("href")}\".");
            }
        }
        else if (current.Count > 1)
        {
            problems.Add($"At most one navigation entry may be the current page, found {current.Count}.");
        }

        return Prefixed(page, problems);
    }

    // ---------------------------------------------------------------- 3. own design system

    /// <summary>No Bootstrap reference, same-origin stylesheets that resolve as text/css and are cacheable.</summary>
    public static IEnumerable<string> StylesheetLinks(UiPageCase page, UiAssets assets)
    {
        var problems = new List<string>();
        if (page.Page.Body.Contains("lib/bootstrap", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("The page still references /lib/bootstrap.");
        }

        var sheets = StylesheetUrls(page.Page).ToList();
        if (sheets.Count == 0)
        {
            problems.Add("The page links no stylesheet.");
        }

        foreach (var sheet in sheets)
        {
            var response = assets.Find(sheet);
            if (!IsSameOrigin(sheet))
            {
                problems.Add($"The stylesheet {sheet} is not served by this site.");
            }
            else if (response is null || response.Status != 200)
            {
                problems.Add($"The stylesheet {sheet.AbsolutePath} does not respond 200 (got {response?.Status.ToString() ?? "no response"}).");
            }
            else
            {
                if (response.Header("Content-Type")?.StartsWith("text/css", StringComparison.OrdinalIgnoreCase) != true)
                {
                    problems.Add($"The stylesheet {sheet.AbsolutePath} is served as \"{response.Header("Content-Type")}\", not text/css.");
                }

                if (response.Header("Cache-Control") is null && response.Header("ETag") is null)
                {
                    problems.Add($"The stylesheet {sheet.AbsolutePath} has neither Cache-Control nor ETag.");
                }
            }
        }

        foreach (var (url, asset, importedFrom) in SheetTree(page.Page, assets).Where(t => t.ImportedFrom is not null))
        {
            if (asset is null || asset.Status != 200)
            {
                problems.Add($"The @import of {url.AbsolutePath} (from {importedFrom!.AbsolutePath}) does not respond 200 (got {asset?.Status.ToString() ?? "no response"}).");
            }
            else if (asset.MediaType != "text/css")
            {
                problems.Add($"The imported stylesheet {url.AbsolutePath} is served as \"{asset.Header("Content-Type")}\", not text/css.");
            }
        }

        return Prefixed(page, problems);
    }

    /// <summary>Dark scheme, reduced motion, focus-visible and a :root block of design tokens; <paramref name="css"/> is the CSS that applies to one page (see <see cref="UiAssets.CssOf"/>).</summary>
    public static IEnumerable<string> DesignSystemCss(string css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            yield return "There is no CSS to inspect.";
            yield break;
        }

        css = StripCssComments(css);
        if (!Regex.IsMatch(css, @"@media[^{]*\(\s*prefers-color-scheme\s*:\s*dark\s*\)"))
        {
            yield return "The CSS has no @media (prefers-color-scheme: dark).";
        }

        if (!Regex.IsMatch(css, @"@media[^{]*\(\s*prefers-reduced-motion\s*:\s*[\w-]+\s*\)"))
        {
            yield return "The CSS has no @media (prefers-reduced-motion ...).";
        }

        if (!css.Contains(":focus-visible", StringComparison.Ordinal))
        {
            yield return "The CSS has no :focus-visible rule.";
        }

        var rootVariables = RootCustomProperties(css);
        if (rootVariables < 10)
        {
            yield return $"The CSS needs a plain :root block with at least 10 custom properties; it declares {rootVariables}.";
        }
    }

    /// <summary>The distinct custom properties declared by rules whose selector list contains a bare <c>:root</c> (<c>:root, :host</c> and <c>:root,[data-theme=light]</c> count; <c>:root[data-theme=dark]</c> does not).</summary>
    private static int RootCustomProperties(string css)
        => Regex.Matches(css, @"(?<selector>[^{}@;]+)\{(?<body>[^{}]*)\}")
            .Where(rule => SelectorList(rule.Groups["selector"].Value).Any(selector => selector.Equals(":root", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(rule => Regex.Matches(rule.Groups["body"].Value, @"(?<![\w-])(--[\w-]+)\s*:").Select(declaration => declaration.Groups[1].Value))
            .Distinct()
            .Count();

    /// <summary>The comma-separated selectors of a rule, split at the top level only (not inside parentheses or brackets), trimmed.</summary>
    private static IEnumerable<string> SelectorList(string selectors)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= selectors.Length; i++)
        {
            if (i < selectors.Length && selectors[i] is '(' or '[')
            {
                depth++;
            }
            else if (i < selectors.Length && selectors[i] is ')' or ']')
            {
                depth--;
            }
            else if (i == selectors.Length || selectors[i] == ',' && depth == 0)
            {
                yield return selectors[start..i].Trim();
                start = i + 1;
            }
        }
    }

    /// <summary>The CSS honours <c>[data-theme="light"]</c> and <c>[data-theme="dark"]</c> overrides on the document root.</summary>
    public static IEnumerable<string> ThemeOverridesCss(string css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            yield return "There is no CSS to inspect.";
            yield break;
        }

        css = StripCssComments(css);
        foreach (var theme in new[] { "light", "dark" })
        {
            if (!Regex.IsMatch(css, $@"\[\s*data-theme\s*=\s*[""']?{theme}[""']?\s*\]"))
            {
                yield return $"The CSS has no [data-theme=\"{theme}\"] override selector.";
            }
        }
    }

    // ---------------------------------------------------------------- 4. forms and controls

    /// <summary>Every visible form control, button, link and summary has an accessible name; ids are unique; img have alt; svg are hidden or labelled.</summary>
    public static IEnumerable<string> ControlNames(UiPageCase page)
    {
        var document = page.Document;
        var problems = new List<string>();

        foreach (var control in Visible(document.QuerySelectorAll("input, select, textarea")))
        {
            var type = control.GetAttribute("type")?.ToLowerInvariant() ?? "text";
            if (control.LocalName == "input" && InputTypesWithoutLabel.Contains(type))
            {
                continue;
            }

            if (AccessibleName(control).Length == 0)
            {
                problems.Add($"A form control has no accessible name (label for/wrapping label/aria-label): {Snippet(control)}");
            }
        }

        foreach (var named in Visible(document.QuerySelectorAll("button, a[href], summary")))
        {
            if (AccessibleName(named).Length == 0)
            {
                problems.Add($"A <{named.LocalName}> has no accessible name (text, aria-label or title): {Snippet(named)}");
            }
        }

        var duplicates = document.QuerySelectorAll("[id]").Select(e => e.Id!).Where(id => id.Length > 0).GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (var id in duplicates)
        {
            problems.Add($"The id \"{id}\" is used more than once.");
        }

        foreach (var image in document.QuerySelectorAll("img").Where(i => !i.HasAttribute("alt")))
        {
            problems.Add($"An <img> has no alt attribute: {Snippet(image)}");
        }

        foreach (var svg in Visible(document.QuerySelectorAll("svg")))
        {
            var decorative = svg.Closest("[aria-hidden=true]") is not null;
            var meaningful = svg.GetAttribute("role") == "img" && SvgName(svg).Length > 0;
            if (!decorative && !meaningful)
            {
                problems.Add($"An inline <svg> must be decorative (aria-hidden=\"true\" on it or an ancestor) or role=\"img\" with a name: {Snippet(svg)}");
            }
        }

        return Prefixed(page, problems);
    }

    // The product pages confirm with "Producto creado.", "Producto guardado.", "Producto eliminado.", "Usuario creado.", "Permiso concedido." and "Permiso revocado.".
    private static readonly Regex SuccessWording = new(@"\b(?:cread|guardad|eliminad|actualizad|concedid|revocad)[oa]s?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsConfirmation(IElement status)
        => status.Matches(PageContract.NoticeSuccess) || status.QuerySelector(PageContract.NoticeSuccess) is not null || status.Closest(PageContract.NoticeSuccess) is not null
           || SuccessWording.IsMatch(Normalize(status.TextContent));

    /// <summary>Errors are announced with role=alert, confirmations with role=status that carry the confirmation message (a notice-success hook or the Spanish success wording); an error never wears the status role.</summary>
    public static IEnumerable<string> LiveRegions(UiPageCase page)
    {
        var document = page.Document;
        var problems = new List<string>();

        var alerts = Visible(document.QuerySelectorAll("[role=alert], [aria-live=assertive]")).Where(a => Normalize(a.TextContent).Length > 0).ToList();
        var statuses = Visible(document.QuerySelectorAll("[role=status], [aria-live=polite]")).Where(s => Normalize(s.TextContent).Length > 0).ToList();

        foreach (var notice in document.QuerySelectorAll(PageContract.NoticeSuccess).Where(n => n.Closest("[role=status], [aria-live=polite]") is null))
        {
            problems.Add($"The success confirmation must be role=\"status\" (or aria-live=\"polite\"): {Snippet(notice)}");
        }

        if (page.ExpectAlert && alerts.Count == 0)
        {
            problems.Add("The error state shows no visible role=\"alert\" (or aria-live=\"assertive\") element with text.");
        }

        if (page.ExpectStatus)
        {
            if (statuses.Count == 0)
            {
                problems.Add("The confirmation state shows no visible role=\"status\" (or aria-live=\"polite\") element with text.");
            }
            else if (!statuses.Any(IsConfirmation))
            {
                problems.Add($"No status element carries the confirmation message (data-testid=\"notice-success\" or the Spanish success wording such as \"Producto creado.\"): {Snippet(statuses[0])}");
            }

            if (alerts.Count > 0)
            {
                problems.Add($"A confirmation page must not announce an error alert: {Snippet(alerts[0])}");
            }
        }

        return Prefixed(page, problems);
    }

    /// <summary>Rejected fields expose their message (aria-invalid + aria-describedby to a message element); pristine pages mark nothing invalid.</summary>
    public static IEnumerable<string> InvalidFields(UiPageCase page)
    {
        var document = page.Document;
        var problems = new List<string>();
        var controls = Visible(document.QuerySelectorAll("input[name], select[name], textarea[name]"))
            .Where(c => c.GetAttribute("type")?.ToLowerInvariant() != "hidden").ToList();

        var expectedInvalid = new List<(IElement Control, string Field)>();
        foreach (var field in page.InvalidFields)
        {
            var control = controls.FirstOrDefault(c => LastSegment(c.GetAttribute("name")!).Equals(field, StringComparison.OrdinalIgnoreCase));
            if (control is null)
            {
                problems.Add($"The rejected form has no visible '{field}' field to inspect.");
                continue;
            }

            expectedInvalid.Add((control, field));
        }

        var invalid = controls.Where(c => c.GetAttribute("aria-invalid") == "true").Concat(expectedInvalid.Select(e => e.Control)).ToHashSet();
        var valid = controls.Where(c => !invalid.Contains(c)).ToList();

        foreach (var (control, field) in expectedInvalid)
        {
            problems.AddRange(ExposesItsError(document, control, field, valid));
        }

        foreach (var control in controls.Where(c => c.GetAttribute("aria-invalid") == "true"))
        {
            problems.AddRange(ExposesItsError(document, control, control.GetAttribute("name")!, valid));
        }

        if (!page.Submitted)
        {
            foreach (var control in controls.Where(c => c.GetAttribute("aria-invalid") == "true"))
            {
                problems.Add($"A pristine form marks '{control.GetAttribute("name")}' as invalid.");
            }
        }

        return Prefixed(page, problems.Distinct());
    }

    private static string[] DescribedByIds(IElement control)
        => (control.GetAttribute("aria-describedby") ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// aria-invalid="true" plus an aria-describedby that reaches a message element of its own: outside any label (the label already names the
    /// field), different from the label's text, and not shared with a field that is valid.
    /// </summary>
    private static IEnumerable<string> ExposesItsError(IDocument document, IElement control, string field, IReadOnlyList<IElement> validControls)
    {
        if (control.GetAttribute("aria-invalid") != "true")
        {
            yield return $"The rejected field '{field}' lacks aria-invalid=\"true\".";
            yield break;
        }

        var labels = document.QuerySelectorAll("label")
            .Where(l => !string.IsNullOrEmpty(control.Id) && l.GetAttribute("for") == control.Id || l.Contains(control)).ToList();
        var targets = DescribedByIds(control).Select(id => document.GetElementById(id)).OfType<IElement>().Where(e => Normalize(e.TextContent).Length > 0).ToList();
        if (targets.Count == 0)
        {
            yield return $"The invalid field '{field}' has no aria-describedby pointing to an element that contains the message (describedby=\"{control.GetAttribute("aria-describedby")}\").";
            yield break;
        }

        var reasons = new List<string>();
        foreach (var target in targets)
        {
            var sharer = validControls.FirstOrDefault(c => DescribedByIds(c).Contains(target.Id));
            if (target.Closest("label") is not null)
            {
                reasons.Add($"aria-describedby of '{field}' points into a <label> (the label text is not the error message): {Snippet(target)}");
            }
            else if (labels.Any(l => Normalize(l.TextContent) == Normalize(target.TextContent)))
            {
                reasons.Add($"aria-describedby of '{field}' repeats the field's own label text instead of the error message: {Snippet(target)}");
            }
            else if (sharer is not null)
            {
                reasons.Add($"aria-describedby of '{field}' points to an element that also describes the valid field '{sharer.GetAttribute("name")}': {Snippet(target)}");
            }
            else
            {
                yield break;
            }
        }

        foreach (var reason in reasons)
        {
            yield return reason;
        }
    }

    private static string LastSegment(string name) => name[(name.LastIndexOfAny(['.', ']']) + 1)..];

    private static bool IsPost(IElement form) => string.Equals(form.GetAttribute("method"), "post", StringComparison.OrdinalIgnoreCase);

    /// <summary>A visible field the person types into or picks (so a confirmation form with only hidden values and a button is not a data-entry form).</summary>
    private static bool HasVisibleTypedField(IElement form)
        => Visible(form.QuerySelectorAll("input, textarea, select"))
            .Any(c => c.TagName != "INPUT" || c.GetAttribute("type")?.ToLowerInvariant() is not ("hidden" or "submit" or "button" or "reset" or "image"));

    /// <summary>
    /// The fields a page of this kind must offer, so that finding no input is a defect and not a pass. A field is demanded only when the page offers a visible,
    /// usable form for it: nothing on a page whose form is hidden or has no submit control (a view-only member, the "someone else changed this product" state).
    /// </summary>
    private static (bool Password, bool Email, bool Price, bool Stock) ExpectedInputs(UiPageCase page)
    {
        var offersProductCreation = ProductFlows.CreateForms(page.Page).Any();
        var offersProductEdition = ProductFlows.EditForms(page.Page).Any();
        var offersUserCreation = page.Document.Forms.Any(f => IsPost(f) && !PageContract.IsLogoutForm(f) && !f.HasAttribute("data-action")
                                                             && PageContract.IsVisibleAndUsable(f) && HasVisibleTypedField(f));
        return page.Type switch
        {
            UiPageType.Login or UiPageType.Register => (true, true, false, false),
            UiPageType.Users => (offersUserCreation, offersUserCreation, false, false),
            UiPageType.Products => (false, false, offersProductCreation, offersProductCreation),
            UiPageType.ProductEdit => (false, false, offersProductEdition, offersProductEdition),
            _ => (false, false, false, false),
        };
    }

    /// <summary>Password, email and numeric inputs carry the right type, autocomplete and inputmode; every page that offers such a form has at least one of each.</summary>
    public static IEnumerable<string> InputHints(UiPageCase page)
    {
        var problems = new List<string>();
        var inputs = Visible(page.Document.QuerySelectorAll("input")).Where(i => i.GetAttribute("type")?.ToLowerInvariant() != "hidden").ToList();

        var expected = ExpectedInputs(page);
        var passwords = inputs.Where(i => i.GetAttribute("type")?.ToLowerInvariant() == "password").ToList();
        if (expected.Password && passwords.Count == 0)
        {
            problems.Add($"InputHints found no password field on this {page.Type} page, which must offer one.");
        }

        foreach (var password in passwords)
        {
            var autocomplete = password.GetAttribute("autocomplete");
            var wanted = page.Type == UiPageType.Login ? "current-password" : "new-password";
            if (page.Type is UiPageType.Login or UiPageType.Register or UiPageType.Users)
            {
                if (autocomplete != wanted)
                {
                    problems.Add($"The password field '{password.GetAttribute("name")}' must have autocomplete=\"{wanted}\" (has \"{autocomplete}\").");
                }
            }
            else if (autocomplete is not ("current-password" or "new-password"))
            {
                problems.Add($"The password field '{password.GetAttribute("name")}' needs autocomplete current-password or new-password.");
            }
        }

        var emails = inputs.Where(i => LastSegment(i.GetAttribute("name") ?? string.Empty).Equals("email", StringComparison.OrdinalIgnoreCase)).ToList();
        if (expected.Email && emails.Count == 0)
        {
            problems.Add($"InputHints found no email field on this {page.Type} page, which must offer one.");
        }

        foreach (var email in emails)
        {
            if (email.GetAttribute("type")?.ToLowerInvariant() != "email")
            {
                problems.Add($"The email field '{email.GetAttribute("name")}' must be type=\"email\".");
            }

            // Only the person's own address is autofilled: the create-user form of /users is about somebody else.
            if (page.Type is UiPageType.Login or UiPageType.Register && email.GetAttribute("autocomplete") is not ("email" or "username"))
            {
                problems.Add($"The email field '{email.GetAttribute("name")}' needs autocomplete=\"email\" or \"username\" (has \"{email.GetAttribute("autocomplete")}\").");
            }
        }

        foreach (var (field, mode, mustExist) in new[] { ("price", "decimal", expected.Price), ("stock", "numeric", expected.Stock) })
        {
            var numerics = inputs.Where(i => LastSegment(i.GetAttribute("name") ?? string.Empty).Equals(field, StringComparison.OrdinalIgnoreCase)
                                             && i.Closest("form") is { } form && IsPost(form)).ToList();
            if (mustExist && numerics.Count == 0)
            {
                problems.Add($"InputHints found no '{field}' field in a POST form on this {page.Type} page, which must offer one.");
            }

            foreach (var numeric in numerics)
            {
                if (numeric.GetAttribute("inputmode") != mode)
                {
                    problems.Add($"The product field '{field}' must have inputmode=\"{mode}\" (has \"{numeric.GetAttribute("inputmode")}\").");
                }
            }
        }

        return Prefixed(page, problems);
    }

    // ---------------------------------------------------------------- 5. tables

    /// <summary>
    /// Caption or label, scoped headers (column headers wherever the table has column semantics; a key/value table with <c>th scope="row"</c> needs none),
    /// right-aligned numeric columns (<c>data-align="end"</c>) and a narrow-screen strategy for the products list (a table in a scroll region, or one card per product).
    /// </summary>
    public static IEnumerable<string> Tables(UiPageCase page)
    {
        var problems = new List<string>();
        foreach (var table in Visible(page.Document.QuerySelectorAll("table")))
        {
            var captioned = Normalize(table.QuerySelector("caption")?.TextContent).Length > 0
                            || !string.IsNullOrWhiteSpace(table.GetAttribute("aria-label")) || !string.IsNullOrWhiteSpace(table.GetAttribute("aria-labelledby"));
            if (!captioned)
            {
                problems.Add($"A <table> has neither a <caption> nor an aria-label: {Snippet(table)}");
            }

            if (HasColumnSemantics(table) && table.QuerySelector("thead th") is null && table.QuerySelector("th[scope=col]") is null)
            {
                problems.Add($"A data <table> has no column headers (<thead> with <th>, or th scope=\"col\"): {Snippet(table)}");
            }

            foreach (var header in table.QuerySelectorAll("th"))
            {
                var inHead = header.Closest("thead") is not null;
                var scope = header.GetAttribute("scope");
                var valid = inHead ? scope == "col" : scope is "row" or "col";
                if (!valid)
                {
                    problems.Add($"A <th> {(inHead ? "in the header must have scope=\"col\"" : "in the body must have scope=\"row\"")} (has \"{scope}\"): {Snippet(header)}");
                }
            }
        }

        if (page.Type == UiPageType.Products && page.Page.Select(PageContract.ProductRow).FirstOrDefault() is { } firstRow)
        {
            problems.AddRange(ProductsList(page, firstRow));
        }

        return Prefixed(page, problems);
    }

    /// <summary>
    /// A table whose columns are headed: not a key/value table (no thead and every row opens with a <c>th scope="row"</c>), and either with two or more data rows
    /// or with a <c>td</c> in a column that has a <c>th</c>.
    /// </summary>
    private static bool HasColumnSemantics(IElement table)
    {
        var rows = table.QuerySelectorAll("tr").ToList();
        var dataRows = rows.Where(r => r.Closest("thead") is null && r.Children.Any(c => c.LocalName == "td")).ToList();
        var keyValue = table.QuerySelector("thead") is null && table.QuerySelector("th[scope=col]") is null && dataRows.Count > 0
                       && dataRows.All(r => r.Children.Any(c => c.LocalName == "th" && c.GetAttribute("scope") == "row"));
        if (keyValue)
        {
            return false;
        }

        var headedColumns = rows.SelectMany(r => r.Children.Select((cell, index) => (cell, index))).Where(c => c.cell.LocalName == "th").Select(c => c.index).ToHashSet();
        var cellInHeadedColumn = rows.SelectMany(r => r.Children.Select((cell, index) => (cell, index))).Any(c => c.cell.LocalName == "td" && headedColumns.Contains(c.index));
        return dataRows.Count >= 2 || cellInHeadedColumn;
    }

    private static IEnumerable<string> ProductsList(UiPageCase page, IElement firstRow)
    {
        var table = firstRow.Closest("table");
        var rows = page.Page.Select(PageContract.ProductRow).ToList();

        foreach (var field in new[] { "price", "stock" })
        {
            foreach (var cell in rows.Select(r => r.QuerySelector(PageContract.Field(field))).OfType<IElement>().Where(c => c.GetAttribute("data-align") != "end"))
            {
                yield return $"The {field} cell must carry data-align=\"end\": {Snippet(cell)}";
            }

            if (table is null)
            {
                continue;
            }

            var cellInRow = firstRow.QuerySelector(PageContract.Field(field));
            var index = cellInRow is null ? -1 : cellInRow.ParentElement!.Children.ToList().IndexOf(cellInRow);
            var header = index < 0 ? null : table.QuerySelectorAll("thead tr").LastOrDefault()?.Children.ElementAtOrDefault(index);
            if (header is null || header.GetAttribute("data-align") != "end")
            {
                yield return $"The header of the {field} column must carry data-align=\"end\": {(header is null ? "no header cell found" : Snippet(header))}";
            }
        }

        var cards = page.Page.Select("[data-testid='product-card']").Count();
        if (table is null)
        {
            if (cards != rows.Count)
            {
                yield return $"The product rows are not in a <table>, so each needs one data-testid=\"product-card\" (found {cards} cards for {rows.Count} rows).";
            }

            yield break;
        }

        var region = table.Closest("[role=region]");
        var scrollRegion = region is not null && (!string.IsNullOrWhiteSpace(region.GetAttribute("aria-label")) || !string.IsNullOrWhiteSpace(region.GetAttribute("aria-labelledby")))
                           && region.GetAttribute("tabindex") == "0";
        if (!scrollRegion && cards != rows.Count)
        {
            yield return $"The products table needs a scroll region wrapper (role=\"region\" + aria-label + tabindex=\"0\") or one data-testid=\"product-card\" per row (found {cards} cards for {rows.Count} rows).";
        }
    }

    // ---------------------------------------------------------------- 6. app-shell integrity and 7. theme toggle

    /// <summary>Exactly one logout form on an authenticated page, none on an anonymous one.</summary>
    public static IEnumerable<string> LogoutForm(UiPageCase page)
    {
        var logouts = page.Document.Forms.Count(PageContract.IsLogoutForm);
        return Prefixed(page, logouts == (page.Authenticated ? 1 : 0)
            ? Array.Empty<string>()
            : new[] { $"Expected {(page.Authenticated ? "exactly one" : "no")} logout form, found {logouts}." });
    }

    /// <summary>The navigation is one element restyled by CSS: no link (same target and text) appears twice inside it.</summary>
    public static IEnumerable<string> NavigationNotDuplicated(UiPageCase page)
    {
        var nav = PrimaryNav(page.Page);
        if (nav is null)
        {
            return Prefixed(page, UiPageTypes.HasShell(page.Type) ? new[] { "There is no primary navigation." } : Array.Empty<string>());
        }

        var problems = nav.QuerySelectorAll("a[href]")
            .GroupBy(a => (Path: Resolve(page.Page, a.GetAttribute("href"))?.PathAndQuery, Text: AccessibleName(a).ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .Select(g => $"The navigation repeats the link \"{g.Key.Text}\" -> {g.Key.Path}; one element restyled by CSS serves desktop and mobile.");
        return Prefixed(page, problems);
    }

    /// <summary>The mobile menu toggle works without JavaScript (details/summary or checkbox + label) and is named.</summary>
    public static IEnumerable<string> MobileMenuToggle(UiPageCase page)
    {
        var nav = PrimaryNav(page.Page);
        return nav is null
            ? Prefixed(page, UiPageTypes.HasShell(page.Type) ? new[] { "There is no primary navigation to toggle." } : Array.Empty<string>())
            : Prefixed(page, MenuToggle(page, nav));
    }

    /// <summary>No inline style attribute anywhere.</summary>
    public static IEnumerable<string> NoInlineStyles(UiPageCase page)
        => Prefixed(page, page.Document.QuerySelectorAll("[style]").Select(styled => $"Inline style attribute: {Snippet(styled)}"));

    /// <summary>At most one tiny, type-less inline script (the theme bootstrap); every script file comes from this site.</summary>
    public static IEnumerable<string> Scripts(UiPageCase page)
    {
        var problems = new List<string>();
        var inline = page.Document.QuerySelectorAll("script:not([src])")
            .Where(s => Normalize(s.GetAttribute("type")).ToLowerInvariant() is "" or "text/javascript" or "module").ToList();
        if (inline.Count > 1)
        {
            problems.Add($"At most one inline script (the theme bootstrap) is allowed, found {inline.Count}.");
        }

        foreach (var script in inline)
        {
            if (script.TextContent.Length >= 600)
            {
                problems.Add($"The inline script is {script.TextContent.Length} characters; the theme bootstrap must stay under 600.");
            }

            if (script.HasAttribute("type"))
            {
                problems.Add("The inline theme-bootstrap script must have no type attribute.");
            }
        }

        foreach (var source in page.Document.QuerySelectorAll("script[src]").Select(s => s.GetAttribute("src")))
        {
            if (Resolve(page.Page, source) is not { } uri || !IsSameOrigin(uri))
            {
                problems.Add($"A script is loaded from another origin: {source}");
            }
        }

        return Prefixed(page, problems);
    }

    private static IEnumerable<string> MenuToggle(UiPageCase page, IElement nav)
    {
        var details = nav.Closest("details");
        if (details is not null)
        {
            var summary = details.Children.FirstOrDefault(c => c.LocalName == "summary");
            if (summary is null)
            {
                yield return "The <details> that holds the navigation has no <summary> toggle.";
            }
            else
            {
                if (AccessibleName(summary).Length == 0)
                {
                    yield return "The menu <summary> has no accessible name.";
                }

                if (summary.HasAttribute("aria-expanded") || details.HasAttribute("aria-expanded"))
                {
                    yield return "<details>/<summary> must not carry aria-expanded (the browser exposes the state).";
                }
            }

            yield break;
        }

        var shellCheckboxes = page.Document.QuerySelectorAll("input[type=checkbox][id]").Where(c => c.Closest("main") is null).ToList();
        var pair = shellCheckboxes.Select(c => (Checkbox: c, Label: page.Document.QuerySelectorAll("label[for]").FirstOrDefault(l => l.GetAttribute("for") == c.Id && l.Closest("main") is null)))
            .FirstOrDefault(p => p.Label is not null);
        if (pair.Label is null)
        {
            yield return "The mobile menu toggle must work without JavaScript: the navigation inside a <details>, or a checkbox with its <label for> in the shell.";
        }
        else if (AccessibleName(pair.Label).Length == 0 || AccessibleName(pair.Checkbox).Length == 0)
        {
            yield return "The checkbox menu toggle and its label need an accessible name.";
        }
    }

    /// <summary>A button[data-theme-toggle] that never submits a form and is named in Spanish.</summary>
    public static IEnumerable<string> ThemeToggle(UiPageCase page)
    {
        var problems = new List<string>();
        var toggles = Visible(page.Document.QuerySelectorAll("[data-theme-toggle]")).ToList();
        if (toggles.Count == 0)
        {
            problems.Add("The shell has no button[data-theme-toggle].");
        }

        foreach (var toggle in toggles)
        {
            if (toggle.LocalName != "button")
            {
                problems.Add($"data-theme-toggle must be on a <button>: {Snippet(toggle)}");
                continue;
            }

            if (toggle.GetAttribute("type") != "button")
            {
                problems.Add($"The theme toggle must be type=\"button\": {Snippet(toggle)}");
            }

            var name = AccessibleName(toggle);
            if (!name.Contains("tema", StringComparison.OrdinalIgnoreCase) && !name.Contains("modo", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"The theme toggle's name must contain \"tema\" or \"modo\" but is \"{name}\".");
            }

            if (toggle.Closest("form") is not null)
            {
                problems.Add("The theme toggle must not sit inside a form.");
            }
        }

        return Prefixed(page, problems);
    }

    // ---------------------------------------------------------------- 8. error answers

    /// <summary>
    /// An HTML page answering 400 (a rejected antiforgery token) is an <c>error</c> page: one data-page="error" region, one h1 and a Spanish message about
    /// the request being invalid or expired; it never says the page was not found. Plain-text 400 bodies are not pages and are not inspected.
    /// Today's antiforgery answer is an empty body labelled <c>text/html</c>, so this rule is red until a production handler renders that page.
    /// </summary>
    public static IEnumerable<string> RejectedRequestPage(UiPageCase page)
    {
        if (page.Page.Status != 400 || page.Page.Header("Content-Type")?.Contains("text/html", StringComparison.OrdinalIgnoreCase) != true)
        {
            return [];
        }

        var problems = new List<string>();
        if (page.Type != UiPageType.Error)
        {
            problems.Add($"An HTML 400 page must be an error-type page, but this one is typed {page.Type}.");
        }

        var regions = page.Document.QuerySelectorAll("[data-page='error']").Count();
        if (regions != 1)
        {
            problems.Add($"A rejected request must show exactly one data-page=\"error\" region, found {regions}.");
        }

        var h1s = Visible(page.Document.QuerySelectorAll("h1")).Count();
        if (h1s != 1)
        {
            problems.Add($"A rejected request page needs exactly one visible h1, found {h1s}.");
        }

        if (Regex.IsMatch(page.Page.Body, "no encontrad", RegexOptions.IgnoreCase))
        {
            problems.Add("A rejected request must not say that something was not found (\"no encontrad...\"): it is an invalid request, not a missing page.");
        }

        if (!Regex.IsMatch(page.Page.Text, @"solicitud|petici[oó]n|formulario", RegexOptions.IgnoreCase) || !Regex.IsMatch(page.Page.Text, @"inv[aá]lid[oa]|no (?:es )?v[aá]lid[oa]|expirad[oa]|caduc|venci", RegexOptions.IgnoreCase))
        {
            problems.Add($"A rejected request page must tell, in Spanish, that the request is invalid or has expired. Page text: {page.Page.Text}");
        }

        return Prefixed(page, problems);
    }

    // ---------------------------------------------------------------- 9. content hooks

    /// <summary>Exactly one data-page region, named after the page type, inside main and holding the h1.</summary>
    public static IEnumerable<string> PageRegion(UiPageCase page) => Prefixed(page, DataPage(page));

    /// <summary>Empty product lists/searches show data-testid="empty-state" (with a call to action for people who can act); lists with products do not.</summary>
    public static IEnumerable<string> EmptyStates(UiPageCase page) => Prefixed(page, EmptyState(page));

    /// <summary>data-stock-status out/low/ok on the stock cell (or a badge in it) of every product row.</summary>
    public static IEnumerable<string> StockStatuses(UiPageCase page) => Prefixed(page, StockStatus(page));

    /// <summary>data-chip="owner" / "permission" on the profile card.</summary>
    public static IEnumerable<string> ProfileChipHooks(UiPageCase page) => Prefixed(page, ProfileChips(page));

    private static IEnumerable<string> DataPage(UiPageCase page)
    {
        var expected = UiPageTypes.DataPage(page.Type);

        // The pager already uses data-page for its page number: only the names of the page types count here.
        var regions = page.Document.QuerySelectorAll("[data-page]").Where(e => UiPageTypes.AllDataPageNames.Contains(e.GetAttribute("data-page")!)).ToList();
        if (regions.Count != 1)
        {
            yield return $"Expected exactly one element data-page=\"{expected}\", found {regions.Count} page regions ({string.Join(", ", regions.Select(r => r.GetAttribute("data-page")))}).";
            yield break;
        }

        var region = regions[0];
        if (region.GetAttribute("data-page") != expected)
        {
            yield return $"The page region says data-page=\"{region.GetAttribute("data-page")}\" but this is a \"{expected}\" page.";
        }

        var main = page.Document.QuerySelector("main, [role=main]");
        if (main is null || region != main && !main.Contains(region))
        {
            yield return "The data-page region must be the <main> or inside it.";
        }

        var h1 = Visible(page.Document.QuerySelectorAll("h1")).FirstOrDefault();
        if (h1 is not null && region != h1 && !region.Contains(h1))
        {
            yield return "The data-page region must contain the page's <h1>.";
        }
    }

    private static IEnumerable<string> EmptyState(UiPageCase page)
    {
        var states = Visible(page.Document.QuerySelectorAll("[data-testid='empty-state']")).ToList();
        if (page.Type != UiPageType.Products || page.Page.Status != 200)
        {
            yield break;
        }

        var rows = page.Page.Select(PageContract.ProductRow).Count();
        if (rows > 0)
        {
            if (states.Count > 0)
            {
                yield return "A list with products must not show data-testid=\"empty-state\".";
            }

            yield break;
        }

        if (states.Count != 1)
        {
            yield return $"An empty product list/search needs exactly one data-testid=\"empty-state\", found {states.Count}.";
            yield break;
        }

        var text = Normalize(string.Concat(states[0].Descendants<IText>().Where(t => t.ParentElement?.Closest("a, button") is null).Select(t => t.Data + " ")));
        if (text.Length == 0)
        {
            yield return "The empty state has no message (text outside its links and buttons).";
        }
        else if (EnglishLeaks.Find(text).Count > 0)
        {
            yield return $"The empty state message is not Spanish: {text}";
        }

        var canAct = ProductFlows.CreateForms(page.Page).Any();
        if (canAct && !states[0].QuerySelectorAll("a[href], button").Any(a => AccessibleName(a).Length > 0))
        {
            yield return "A person who can create products needs a call to action (link or button) inside the empty state.";
        }
    }

    private static IEnumerable<string> StockStatus(UiPageCase page)
    {
        if (page.Type != UiPageType.Products)
        {
            yield break;
        }

        foreach (var row in page.Page.Select(PageContract.ProductRow))
        {
            var cell = row.QuerySelector(PageContract.Field("stock"));
            if (cell is null || !int.TryParse(cell.GetAttribute("data-stock"), out var stock))
            {
                yield return $"A product row has no stock cell with data-stock: {Snippet(row)}";
                continue;
            }

            var chips = cell.QuerySelectorAll("[data-stock-status]").ToList();
            if (cell.HasAttribute("data-stock-status"))
            {
                chips.Add(cell);
            }

            var expected = stock == 0 ? "out" : stock <= 5 ? "low" : "ok";
            if (chips.Count != 1 || chips[0].GetAttribute("data-stock-status") != expected)
            {
                yield return $"The stock {stock} must carry exactly one data-stock-status=\"{expected}\" on the stock cell or a badge inside it (found {string.Join(",", chips.Select(c => c.GetAttribute("data-stock-status")))}).";
            }
            else if (chips[0] != cell && Normalize(chips[0].TextContent).Length == 0 || Normalize(cell.TextContent).Length == 0)
            {
                yield return $"The stock status of {stock} has no visible text.";
            }
        }
    }

    private static IEnumerable<string> ProfileChips(UiPageCase page)
    {
        if (page.Type != UiPageType.Home || page.Page.Document.QuerySelector(PageContract.ProfileTenantId) is null)
        {
            yield break;
        }

        var isOwner = page.Page.Document.QuerySelector(PageContract.ProfileIsOwner)?.GetAttribute("data-value") == "true";
        var ownerChips = page.Page.Select("[data-chip='owner']").Count();
        if (ownerChips != (isOwner ? 1 : 0))
        {
            yield return $"Expected {(isOwner ? "one" : "no")} data-chip=\"owner\" for {(isOwner ? "an owner" : "a member")}, found {ownerChips}.";
        }

        var held = page.Page.Select(PageContract.ProfilePermission).ToList();
        var chips = page.Page.Select("[data-chip='permission']").ToList();
        if (chips.Count != held.Count)
        {
            yield return $"Every held permission needs a data-chip=\"permission\": {held.Count} permissions, {chips.Count} chips.";
        }

        foreach (var permission in held)
        {
            var chip = permission.HasAttribute("data-chip") ? permission : permission.QuerySelector("[data-chip='permission']");
            var label = Conversia.Web.IntegrationTests.Infrastructure.SpanishText.PermissionLabels[Enum.Parse<Conversia.Domain.Enums.Permission>(permission.GetAttribute("data-permission")!)];
            if (chip is null || !Normalize(chip.TextContent).Contains(label, StringComparison.OrdinalIgnoreCase))
            {
                yield return $"The permission {permission.GetAttribute("data-permission")} needs a data-chip=\"permission\" chip showing \"{label}\".";
            }
        }
    }

    // ---------------------------------------------------------------- diagnostics

    private static string Describe(IElement? element) => element is null ? "missing" : Snippet(element);

    private static string Snippet(IElement element)
    {
        var html = element.OuterHtml.Replace('\n', ' ').Replace('\r', ' ');
        html = Regex.Replace(html, @"\s+", " ");
        return html.Length <= 160 ? html : html[..160] + "...";
    }
}
