using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>
/// The structure rules are only worth anything if they can fail, and only worth keeping if they do not block legitimate markup. These tests
/// feed <see cref="UiStructureAssertions"/> small hand-written pages (no host, no database): a compliant page of each kind must pass EVERY
/// rule; each mutation breaks exactly one thing and the named rule must report that very problem (its message is asserted, not just "something");
/// each accepted variant is modern markup that must NOT be reported. A mutation whose target text is not in the page fails the test, so none can
/// silently do nothing.
/// </summary>
public class UiStructureAssertionsTests
{
    private const string Tokens = "--c1:1;--c2:2;--c3:3;--c4:4;--c5:5;--c6:6;--c7:7;--c8:8;--c9:9;--c10:10";

    private const string Css = """
        :root{--c1:1;--c2:2;--c3:3;--c4:4;--c5:5;--c6:6;--c7:7;--c8:8;--c9:9;--c10:10}
        @media (prefers-color-scheme: dark){:root:not([data-theme="light"]){--c1:0}}
        :root[data-theme="dark"]{--c1:0}
        :root[data-theme="light"]{--c1:1}
        @media (prefers-reduced-motion: reduce){*{animation:none}}
        :focus-visible{outline:2px solid}
        """;

    private static readonly IReadOnlyDictionary<string, Func<UiPageCase, UiAssets, IEnumerable<string>>> Rules = new Dictionary<string, Func<UiPageCase, UiAssets, IEnumerable<string>>>
    {
        ["document"] = UiStructureAssertions.Document,
        ["landmarks"] = (p, _) => UiStructureAssertions.Landmarks(p),
        ["in-page-links"] = (p, _) => UiStructureAssertions.InPageLinks(p),
        ["headings"] = (p, _) => UiStructureAssertions.Headings(p),
        ["navigation"] = (p, _) => UiStructureAssertions.Navigation(p),
        ["stylesheet-links"] = UiStructureAssertions.StylesheetLinks,
        ["design-css"] = (p, assets) => UiStructureAssertions.DesignSystemCss(assets.CssOf(p.Page)),
        ["theme-css"] = (p, assets) => UiStructureAssertions.ThemeOverridesCss(assets.CssOf(p.Page)),
        ["control-names"] = (p, _) => UiStructureAssertions.ControlNames(p),
        ["live-regions"] = (p, _) => UiStructureAssertions.LiveRegions(p),
        ["invalid-fields"] = (p, _) => UiStructureAssertions.InvalidFields(p),
        ["input-hints"] = (p, _) => UiStructureAssertions.InputHints(p),
        ["tables"] = (p, _) => UiStructureAssertions.Tables(p),
        ["logout"] = (p, _) => UiStructureAssertions.LogoutForm(p),
        ["nav-duplicates"] = (p, _) => UiStructureAssertions.NavigationNotDuplicated(p),
        ["menu-toggle"] = (p, _) => UiStructureAssertions.MobileMenuToggle(p),
        ["inline-styles"] = (p, _) => UiStructureAssertions.NoInlineStyles(p),
        ["scripts"] = (p, _) => UiStructureAssertions.Scripts(p),
        ["theme-toggle"] = (p, _) => UiStructureAssertions.ThemeToggle(p),
        ["rejected-request"] = (p, _) => UiStructureAssertions.RejectedRequestPage(p),
        ["page-region"] = (p, _) => UiStructureAssertions.PageRegion(p),
        ["empty-state"] = (p, _) => UiStructureAssertions.EmptyStates(p),
        ["stock-status"] = (p, _) => UiStructureAssertions.StockStatuses(p),
        ["chips"] = (p, _) => UiStructureAssertions.ProfileChipHooks(p),
    };

    // ---- the compliant pages ----

    private const string RegionOpen = """<div role="region" aria-label="Lista de productos" tabindex="0">""";

    private const string Thead = """<thead><tr><th scope="col">SKU</th><th scope="col">Nombre</th><th scope="col" data-align="end">Precio</th><th scope="col" data-align="end">Stock</th><th scope="col"><span>Acciones</span></th></tr></thead>""";

    private const string TableHtml = """
        <table><caption>Lista de productos</caption>
        """ + Thead + """

        <tbody>
        <tr data-testid="product-row" data-product-id="11111111-1111-1111-1111-111111111111"><td data-field="sku">A-1</td><td data-field="name">Tornillo</td><td data-field="price" data-price="1.50" data-align="end">1,50</td><td data-field="stock" data-stock="0" data-align="end"><span data-stock-status="out">Agotado</span></td><td><a href="/products/11111111-1111-1111-1111-111111111111/edit">Editar</a></td></tr>
        <tr data-testid="product-row" data-product-id="22222222-2222-2222-2222-222222222222"><td data-field="sku">A-2</td><td data-field="name">Martillo</td><td data-field="price" data-price="9.00" data-align="end">9,00</td><td data-field="stock" data-stock="5" data-align="end"><span data-stock-status="low">Pocas unidades</span></td><td><a href="/products/22222222-2222-2222-2222-222222222222/edit">Editar</a></td></tr>
        <tr data-testid="product-row" data-product-id="33333333-3333-3333-3333-333333333333"><td data-field="sku">A-3</td><td data-field="name">Llave</td><td data-field="price" data-price="19.00" data-align="end">19,00</td><td data-field="stock" data-stock="6" data-align="end"><span data-stock-status="ok">Disponible</span></td><td><a href="/products/33333333-3333-3333-3333-333333333333/edit">Editar</a></td></tr>
        </tbody></table>
        """;

    private const string Rows = RegionOpen + TableHtml + "</div>";

    private const string Cards = """
        <ul>
        <li data-testid="product-card"><div data-testid="product-row" data-product-id="11111111-1111-1111-1111-111111111111"><span data-field="sku">A-1</span> <span data-field="name">Tornillo</span> <span data-field="price" data-price="1.50" data-align="end">1,50</span> <span data-field="stock" data-stock="0" data-align="end"><span data-stock-status="out">Agotado</span></span> <a href="/products/11111111-1111-1111-1111-111111111111/edit">Editar</a></div></li>
        <li data-testid="product-card"><div data-testid="product-row" data-product-id="22222222-2222-2222-2222-222222222222"><span data-field="sku">A-2</span> <span data-field="name">Martillo</span> <span data-field="price" data-price="9.00" data-align="end">9,00</span> <span data-field="stock" data-stock="5" data-align="end"><span data-stock-status="low">Pocas unidades</span></span> <a href="/products/22222222-2222-2222-2222-222222222222/edit">Editar</a></div></li>
        <li data-testid="product-card"><div data-testid="product-row" data-product-id="33333333-3333-3333-3333-333333333333"><span data-field="sku">A-3</span> <span data-field="name">Llave</span> <span data-field="price" data-price="19.00" data-align="end">19,00</span> <span data-field="stock" data-stock="6" data-align="end"><span data-stock-status="ok">Disponible</span></span> <a href="/products/33333333-3333-3333-3333-333333333333/edit">Editar</a></div></li>
        </ul>
        """;

    private const string CardsOnly = """
        <ul>
        <li data-testid="product-card">Tornillo</li>
        <li data-testid="product-card">Martillo</li>
        <li data-testid="product-card">Llave</li>
        </ul>
        """;

    private const string Sprites = """<svg aria-hidden="true"><use href="/icons.svg#x"/></svg><span aria-hidden="true"><svg viewBox="0 0 1 1"><path d="M0 0"/></svg></span>""";

    private const string EmptyState = """<div data-testid="empty-state"><p>Todavía no hay productos.</p><a href="/products#sku">Agregar el primer producto</a></div>""";

    private const string CreateForm = """
        <h2>Agregar un producto</h2>
        <form method="post" action="/products"><input type="hidden" name="__RequestVerificationToken" value="x">
        <label for="sku">SKU</label><input id="sku" name="Create.Sku" autocomplete="off">
        <label for="name">Nombre</label><input id="name" name="Create.Name" autocomplete="off">
        <label for="price">Precio</label><input id="price" name="Create.Price" inputmode="decimal" autocomplete="off">
        <label for="stock">Stock</label><input id="stock" name="Create.Stock" inputmode="numeric" autocomplete="off">
        <button type="submit">Crear</button></form>
        """;

    private const string ThemeButton = """<button type="button" data-theme-toggle aria-label="Cambiar tema"><svg aria-hidden="true" focusable="false" viewBox="0 0 1 1"></svg></button>""";

    private const string LogoutFormHtml = """<form method="post" action="/logout"><input type="hidden" name="__RequestVerificationToken" value="x"><button type="submit"><svg aria-hidden="true" focusable="false"></svg>Cerrar sesión</button></form>""";

    /// <summary>The skip link as the real pages render it under <c>&lt;base href="/"&gt;</c>: a path of the very page it sits on plus the fragment (a bare <c>#contenido</c> would leave for <c>/#contenido</c>).</summary>
    private static string SkipLinkOf(string path) => $"""<a href="{path}#contenido">Saltar al contenido</a>""";

    private const string SkipLink = """<a href="/products#contenido">Saltar al contenido</a>""";

    private const string Head = """
        <meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="color-scheme" content="light dark">
        <meta name="description" content="Gestión de productos"><title>Productos</title><base href="/">
        <link rel="stylesheet" href="app.css"><link rel="icon" type="image/svg+xml" href="icon.svg">
        """;

    /// <summary>The app shell around a main content (the page served at <paramref name="path"/>): skip link, banner with the primary navigation, theme toggle and (signed in) logout, main, footer.</summary>
    private static string Shell(string mainContent, string current, string path, bool checkbox = false, bool signedIn = true)
    {
        string Link(string href, string text) => $"""<a href="{href}"{(href == current ? " aria-current=\"page\"" : string.Empty)}>{text}</a>""";
        var links = $"{Link("/", "Inicio")}{Link("/demo", "Recorrido")}{Link("/products", "Productos")}{Link("/users", "Usuarios")}";
        var menu = checkbox
            ? $"""<input type="checkbox" id="menu-toggle"><label for="menu-toggle">Menú</label><nav aria-label="Navegación principal">{links}</nav>"""
            : $"""<details><summary>Menú</summary><nav aria-label="Navegación principal">{links}</nav></details>""";
        return $$"""
            <!DOCTYPE html>
            <html lang="es"><head>
            {{Head}}
            <script>try{var t=localStorage.getItem('tema');if(t)document.documentElement.dataset.theme=t}catch(e){}</script>
            </head><body>
            {{SkipLinkOf(path)}}
            <header>
            {{menu}}
            {{ThemeButton}}
            {{(signedIn ? LogoutFormHtml : string.Empty)}}
            </header>
            <main id="contenido">{{mainContent}}</main>
            <footer><p>Conversia</p></footer>
            </body></html>
            """;
    }

    private static string ProductsMain(string content, string name = "products") => $"""<div data-page="{name}"><h1>Productos</h1><form method="get" action="/products" data-testid="product-search"><label for="search">Buscar</label><input id="search" name="search" type="search"><button type="submit">Buscar</button></form>{CreateForm}{content}</div>""";

    private const string LoginAlert = """<div role="alert">Correo o contraseña incorrectos.</div>""";

    private const string EmailInvalidAttributes = """ aria-invalid="true" aria-describedby="email-error">""";

    private const string EmailError = """<div id="email-error">Introduce un correo válido.</div>""";

    private const string PasswordInput = """<input id="password" name="Input.Password" type="password" autocomplete="current-password">""";

    private const string LoginPage = """
        <!DOCTYPE html>
        <html lang="es"><head>
        <meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="color-scheme" content="light dark">
        <meta name="description" content="Inicia sesión"><title>Iniciar sesión</title><base href="/">
        <link rel="stylesheet" href="app.css"><link rel="icon" type="image/svg+xml" href="icon.svg">
        </head><body>
        <a href="/login#contenido">Saltar al contenido</a>
        <main id="contenido"><div data-page="login"><h1>Iniciar sesión</h1>
        <div role="alert">Correo o contraseña incorrectos.</div>
        <form method="post" action="/login">
        <label for="email" id="email-label">Correo electrónico</label><input id="email" name="Input.Email" type="email" autocomplete="username" aria-invalid="true" aria-describedby="email-error"><div id="email-error">Introduce un correo válido.</div>
        <label for="password">Contraseña</label><input id="password" name="Input.Password" type="password" autocomplete="current-password">
        <button type="submit">Iniciar sesión</button></form></div></main>
        <button type="button" data-theme-toggle><span aria-hidden="true">◐</span> Cambiar modo de color</button>
        </body></html>
        """;

    /// <summary>The sign-in throttled by the API (429): the form is re-rendered with an alert and no field is invalid.</summary>
    private static readonly string LoginThrottled = Edit(Edit(Edit(LoginPage, LoginAlert, """<div role="alert">Demasiados intentos. Vuelve a intentarlo en 37 segundos.</div>"""), EmailInvalidAttributes, ">"), EmailError, string.Empty);

    /// <summary>Both fields rejected locally (empty form), each with its own message.</summary>
    private static readonly string LoginEmpty = Edit(Edit(LoginPage, LoginAlert, string.Empty), PasswordInput,
        """<input id="password" name="Input.Password" type="password" autocomplete="current-password" aria-invalid="true" aria-describedby="password-error"><div id="password-error">Introduce tu contraseña.</div>""");

    private const string KeyValueTable = """<table><caption>Datos del espacio</caption><tbody><tr><th scope="row">Espacio</th><td>abc</td></tr><tr><th scope="row">Rol</th><td>Propietario</td></tr></tbody></table>""";

    private const string HomeMain = """
        <div data-page="home"><h1>Tu perfil</h1><dl><dt>Espacio</dt><dd data-testid="profile-tenant-id">abc</dd>
        <dt>Propietario</dt><dd data-testid="profile-is-owner" data-value="true"><span data-chip="owner">Propietario</span></dd>
        <dt>Permisos</dt><dd><ul><li data-testid="profile-permission" data-permission="ProductsView"><span data-chip="permission">Ver productos</span></li></ul></dd></dl>
        """ + KeyValueTable + "</div>";

    private const string ForbiddenMain = """<div data-page="forbidden"><h1>Acceso denegado</h1><p>No tienes permiso para ver esta página.</p><a href="/">Volver al inicio</a></div>""";

    private const string NotFoundMain = """<div data-page="not-found"><h1>Página no encontrada</h1><p>Lo sentimos, la página que buscas no existe.</p></div>""";

    private const string ErrorMain = """<div data-page="error"><h1>Ha ocurrido un error</h1><p>Inténtalo de nuevo más tarde.</p></div>""";

    private const string InvalidRequestMessage = """<p>La solicitud no es válida o ha caducado. Vuelve a cargar la página e inténtalo de nuevo.</p>""";

    private const string InvalidRequestHeading = "<h1>No se pudo completar la acción</h1>";

    private const string InvalidRequestMain = """<div data-page="error">""" + InvalidRequestHeading + InvalidRequestMessage + "</div>";

    private sealed record Fixture(
        string Html,
        UiPageType Type,
        bool Authenticated = false,
        bool Submitted = false,
        bool Alert = false,
        bool Status = false,
        string[]? Invalid = null,
        string? Path = null,
        HttpStatusCode Code = HttpStatusCode.OK);

    private const string PageTwo = "/products?page=2&search=ab";

    private static readonly IReadOnlyDictionary<string, Fixture> Fixtures = new Dictionary<string, Fixture>
    {
        ["products"] = new(Shell(ProductsMain(Rows), "/products", "/products"), UiPageType.Products, Authenticated: true, Path: "/products"),
        ["products-empty"] = new(Shell(ProductsMain(EmptyState), "/products", "/products"), UiPageType.Products, Authenticated: true, Path: "/products"),
        ["products-page-two"] = new(Shell(ProductsMain(Rows), "/products", PageTwo), UiPageType.Products, Authenticated: true, Path: PageTwo),
        ["products-confirmed"] = new(Shell(ProductsMain("""<div role="status" data-testid="notice-success">Producto creado.</div>""" + Rows), "/products", "/products-confirmed"), UiPageType.Products, Authenticated: true, Submitted: true, Status: true, Path: "/products-confirmed"),
        ["products-cards"] = new(Shell(ProductsMain(Cards), "/products", "/products-cards"), UiPageType.Products, Authenticated: true, Path: "/products-cards"),
        ["products-table-with-cards"] = new(Shell(ProductsMain(TableHtml + CardsOnly), "/products", "/products-table-with-cards"), UiPageType.Products, Authenticated: true, Path: "/products-table-with-cards"),
        ["products-sprites"] = new(Shell(ProductsMain(Rows + Sprites), "/products", "/products-sprites"), UiPageType.Products, Authenticated: true, Path: "/products-sprites"),
        ["products-checkbox-menu"] = new(Shell(ProductsMain(Rows), "/products", "/products-checkbox-menu", checkbox: true), UiPageType.Products, Authenticated: true, Path: "/products-checkbox-menu"),
        ["login-rejected"] = new(LoginPage, UiPageType.Login, Submitted: true, Alert: true, Invalid: ["email"], Path: "/login"),
        ["login-empty"] = new(LoginEmpty, UiPageType.Login, Submitted: true, Invalid: ["email", "password"], Path: "/login"),
        ["login-throttled"] = new(LoginThrottled, UiPageType.Login, Submitted: true, Alert: true, Path: "/login"),
        ["home-owner"] = new(Shell(HomeMain, "/", "/"), UiPageType.Home, Authenticated: true, Path: "/"),
        ["forbidden-users"] = new(Shell(ForbiddenMain, "/users", "/users"), UiPageType.Forbidden, Authenticated: true, Path: "/users", Code: HttpStatusCode.Forbidden),
        ["forbidden-products"] = new(Shell(ForbiddenMain, "/products", "/products"), UiPageType.Forbidden, Authenticated: true, Path: "/products", Code: HttpStatusCode.Forbidden),
        ["not-found"] = new(Shell(NotFoundMain, "/", "/esta-pagina-no-existe"), UiPageType.NotFound, Authenticated: true, Path: "/esta-pagina-no-existe", Code: HttpStatusCode.NotFound),
        ["error"] = new(Shell(ErrorMain, string.Empty, "/Error", signedIn: false), UiPageType.Error, Path: "/Error", Code: HttpStatusCode.InternalServerError),
        ["invalid-request"] = new(Shell(InvalidRequestMain, string.Empty, "/login", signedIn: false), UiPageType.Error, Submitted: true, Path: "/login", Code: HttpStatusCode.BadRequest),
        ["invalid-request-signed-in"] = new(Shell(InvalidRequestMain, string.Empty, "/users"), UiPageType.Error, Authenticated: true, Submitted: true, Path: "/users", Code: HttpStatusCode.BadRequest),
    };

    private static string Edit(string html, string from, string to)
        => html.Contains(from, StringComparison.Ordinal) ? html.Replace(from, to, StringComparison.Ordinal) : throw new InvalidOperationException($"The fixture does not contain: {from}");

    private static UiPageCase CaseOf(string fixture, string? html = null, UiPageType? type = null)
    {
        var f = Fixtures[fixture];
        return new UiPageCase
        {
            Label = fixture,
            Page = Respond(f.Path ?? "/" + fixture, f.Code, html ?? f.Html, "text/html; charset=utf-8"),
            Type = type ?? f.Type,
            Authenticated = f.Authenticated,
            Submitted = f.Submitted,
            ExpectAlert = f.Alert,
            ExpectStatus = f.Status,
            InvalidFields = f.Invalid ?? [],
        };
    }

    private static Uri Url(string path) => new(WebTestHost.BaseAddress, path);

    private static PageResponse Respond(string path, HttpStatusCode status, string body, string? contentType = null, bool cacheable = false)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        if (contentType is not null)
        {
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (cacheable)
        {
            response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        }

        return new PageResponse(Url(path), response, body);
    }

    private static UiAsset Asset(string path, HttpStatusCode status, string body, string? contentType = null, bool cacheable = false)
        => new(Respond(path, status, body, contentType, cacheable), Encoding.UTF8.GetBytes(body));

    private static UiAsset Asset(string path, byte[] bytes, string contentType)
        => new(Respond(path, HttpStatusCode.OK, string.Empty, contentType, cacheable: true), bytes);

    /// <summary>The files the fixtures link: app.css, an svg icon, and a favicon.png that really resolves (with the template's default bytes) so that a mutation pointing at it is judged on the bytes alone.</summary>
    private static UiAssets Assets(HttpStatusCode cssStatus = HttpStatusCode.OK, string cssType = "text/css", bool cssCacheable = true, bool icon = true, string css = Css, Action<Dictionary<Uri, UiAsset>>? tweak = null)
    {
        var files = new Dictionary<Uri, UiAsset>
        {
            [Url("/app.css")] = Asset("/app.css", cssStatus, css, cssType, cssCacheable),
            [Url("/favicon.png")] = Asset("/favicon.png", DefaultFavicon.Bytes, "image/png"),
        };
        if (icon)
        {
            files[Url("/icon.svg")] = Asset("/icon.svg", HttpStatusCode.OK, "<svg/>", "image/svg+xml", true);
        }

        tweak?.Invoke(files);
        return new UiAssets(files);
    }

    private static List<string> Problems(string rule, UiPageCase page, UiAssets? assets = null) => Rules[rule](page, assets ?? Assets()).ToList();

    private static void AssertReports(IReadOnlyList<string> problems, string expected)
        => Assert.True(problems.Any(p => p.Contains(expected, StringComparison.Ordinal)),
            $"Expected a problem containing \"{expected}\" but got:{Environment.NewLine}{(problems.Count == 0 ? "(none)" : string.Join(Environment.NewLine, problems))}");

    private static void AssertNoProblems(IReadOnlyList<string> problems)
        => Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

    private static string Broken(string fixture, params (string From, string To)[] changes)
    {
        var html = Fixtures[fixture].Html;
        foreach (var (from, to) in changes)
        {
            Assert.Contains(from, html); // the mutation really changes the page
            html = html.Replace(from, to, StringComparison.Ordinal);
        }

        Assert.NotEqual(Fixtures[fixture].Html, html);
        return html;
    }

    // ---- every rule accepts the compliant pages ----

    public static TheoryData<string> FixtureNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Fixtures.Keys)
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void A_compliant_page_passes_every_rule(string fixture)
    {
        var problems = Rules.SelectMany(rule => Problems(rule.Key, CaseOf(fixture)).Select(p => $"{rule.Key}: {p}")).ToList();

        AssertNoProblems(problems);
    }

    [Fact]
    public void The_compliant_css_passes_the_css_rules()
    {
        Assert.Empty(UiStructureAssertions.DesignSystemCss(Css));
        Assert.Empty(UiStructureAssertions.ThemeOverridesCss(Css));
    }

    // ---- each mutation is caught by its rule, with the message of that very defect ----

    public static TheoryData<string, string, string, string, string> Mutations => new()
    {
        // 1. document
        { "products", "document", """<html lang="es">""", """<html lang="en">""", "<html lang> must be \"es\"" },
        { "products", "document", """<meta charset="utf-8">""", "", "There is no <meta charset>." },
        { "products", "document", """<meta name="viewport" content="width=device-width, initial-scale=1">""", """<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">""", "must not cap zoom" },
        { "products", "document", """<meta name="viewport" content="width=device-width, initial-scale=1">""", """<meta name="viewport" content="width=device-width, initial-scale=1, user-scalable=no">""", "must not disable zoom" },
        { "products", "document", """<meta name="viewport" content="width=device-width, initial-scale=1">""", "", "There is no <meta name=\"viewport\">." },
        { "products", "document", "<title>Productos</title>", "<title> </title>", "The <title> is empty." },
        { "products", "document", """<meta name="color-scheme" content="light dark">""", "", "Neither <meta name=\"color-scheme\"" },
        { "products", "document", """<meta name="color-scheme" content="light dark">""", """<meta name="color-scheme" content="dark">""", "Neither <meta name=\"color-scheme\"" },
        { "products", "document", """<meta name="color-scheme" content="light dark">""", """<meta name="theme-color" content="#000">""", "Neither <meta name=\"color-scheme\"" },
        { "products", "document", """<meta name="color-scheme" content="light dark">""", """<meta name="theme-color" content="#000" media="(prefers-color-scheme: dark)">""", "Neither <meta name=\"color-scheme\"" },
        { "login-rejected", "document", """<meta name="description" content="Inicia sesión">""", "", "A public page needs a non-empty <meta name=\"description\">" },
        { "products", "document", """<link rel="icon" type="image/svg+xml" href="icon.svg">""", "", "There is no <link rel=\"icon\">." },
        { "products", "document", """href="icon.svg">""", """href="favicon.png">""", "default favicon.png" },
        { "products", "document", """href="icon.svg">""", """href="/no-existe.svg">""", "does not resolve with 200" },
        { "products", "document", """href="icon.svg">""", """href="data:text/html,hola">""", "is not an image/*" },
        // 2. landmarks, headings, navigation
        { "products", "landmarks", """<main id="contenido">""", "<main>", "The <main> has no id" },
        { "products", "landmarks", "</main>", "</main><main></main>", "Expected exactly one <main>, found 2" },
        { "products", "landmarks", SkipLink, """<a href="/products#contenido">Ir al contenido</a>""", "The skip link text must contain \"Saltar\"" },
        { "products", "landmarks", SkipLink, """<a href="/products#otro">Saltar al contenido</a>""", "The first focusable link must point to the main landmark" },
        { "products", "landmarks", SkipLink, """<a href="/products#otro">Saltar al contenido</a>""", "must resolve to /products#contenido" },
        { "products", "landmarks", SkipLink, """<a href="/x">Inicio rápido</a><a href="/products#contenido">Saltar al contenido</a>""", "The first focusable link must point to the main landmark" },
        { "products", "landmarks", SkipLink, """<a href="/products#contenido" hidden>Saltar al contenido</a>""", "The first focusable element of the body must be the skip link" },
        { "products", "landmarks", SkipLink, """<div hidden><a href="/products#contenido">Saltar al contenido</a></div>""", "The first focusable element of the body must be the skip link" },
        { "products", "landmarks", SkipLink, """<div tabindex="0">Antes</div><a href="/products#contenido">Saltar al contenido</a>""", "The first focusable element of the body must be the skip link" },
        { "products", "landmarks", SkipLink, """<button type="button">Antes</button><a href="/products#contenido">Saltar al contenido</a>""", "The first focusable element of the body must be the skip link" },
        // the skip link is resolved against <base href="/">: a bare #contenido leaves for /#contenido, whatever page it sits on
        { "products", "landmarks", SkipLink, """<a href="#contenido">Saltar al contenido</a>""", "The first focusable link must point to the main landmark" },
        { "products", "landmarks", SkipLink, """<a href="#contenido">Saltar al contenido</a>""", "resolves (against https://localhost/) to https://localhost/#contenido, which leaves the page: an in-page link must resolve to /products#contenido" },
        { "products", "landmarks", SkipLink, """<a href="/#contenido">Saltar al contenido</a>""", "which leaves the page: an in-page link must resolve to /products#contenido" },
        { "products", "landmarks", SkipLink, """<a href="/products?page=2#contenido">Saltar al contenido</a>""", "which leaves the page: an in-page link must resolve to /products#contenido" },
        { "products-page-two", "landmarks", SkipLinkOf(PageTwo), SkipLinkOf("/products"), "which leaves the page: an in-page link must resolve to /products?page=2&search=ab#contenido" },
        { "products-page-two", "landmarks", SkipLinkOf(PageTwo), """<a href="#contenido">Saltar al contenido</a>""", "which leaves the page: an in-page link must resolve to /products?page=2&search=ab#contenido" },
        { "products", "landmarks", SkipLink, """<a href="/products">Saltar al contenido</a>""", "has an empty fragment" },
        { "products", "landmarks", SkipLink, """<a href="https://example.test/products#contenido">Saltar al contenido</a>""", "which leaves the page" },
        { "products", "landmarks", "<header>", "<div>", "The app shell has no banner" },
        { "products", "landmarks", "<footer>", "<div>", "The app shell has no contentinfo" },
        { "products", "landmarks", """<nav aria-label="Navegación principal">""", "<nav>", "A <nav> has no aria-label" },
        { "products", "headings", "<h1>Productos</h1>", "<h1>Productos</h1><h1>Otra vez</h1>", "Expected exactly one <h1>, found 2" },
        { "products", "headings", "<h1>Productos</h1>", "", "Expected exactly one <h1>, found 0" },
        { "products", "headings", "<h1>Productos</h1>", "<h1></h1>", "The <h1> is empty." },
        { "products", "headings", "<h2>Agregar un producto</h2>", "<h3>Agregar un producto</h3>", "Heading level skipped: h1 is followed by h3" },
        { "products", "headings", "<h2>Agregar un producto</h2>", "<h2>Agregar un producto</h2><h4>Detalle</h4>", "Heading level skipped: h2 is followed by h4" },
        { "products", "navigation", """<a href="/products" aria-current="page">""", """<a href="/products">""", "Expected exactly one navigation entry with aria-current=\"page\" (for /products), found 0" },
        { "products", "navigation", """<a href="/users">""", """<a href="/users" aria-current="page">""", "Expected exactly one navigation entry with aria-current=\"page\" (for /products), found 2" },
        { "products", "navigation", """<a href="/products" aria-current="page">""", """<a href="/products" aria-current="true">""", "aria-current in the navigation must be exactly \"page\"" },
        { "forbidden-users", "navigation", """<a href="/users" aria-current="page">""", """<a href="/users">""", "Expected exactly one navigation entry with aria-current=\"page\" (for /users), found 0" },
        { "forbidden-products", "navigation", """<a href="/products" aria-current="page">""", """<a href="/" aria-current="page">""", "The current navigation entry must link to /products" },
        // 4. controls
        { "products", "control-names", """<label for="sku">SKU</label>""", "", "A form control has no accessible name" },
        { "products", "control-names", """<label for="sku">SKU</label>""", """<label for="sku"> </label>""", "A form control has no accessible name" },
        { "products", "control-names", """<button type="submit">Crear</button>""", """<button type="submit"> </button>""", "A <button> has no accessible name" },
        { "products", "control-names", """<a href="/products/11111111-1111-1111-1111-111111111111/edit">Editar</a>""", """<a href="/products/11111111-1111-1111-1111-111111111111/edit"><svg aria-hidden="true"></svg></a>""", "A <a> has no accessible name" },
        { "products", "control-names", "<summary>Menú</summary>", "<summary></summary>", "A <summary> has no accessible name" },
        { "products", "control-names", """<label for="name">""", """<label for="sku">""", "A form control has no accessible name" },
        { "products", "control-names", "<p>Conversia</p>", """<p id="sku">Conversia</p>""", "The id \"sku\" is used more than once." },
        { "products", "control-names", "<footer>", """<footer><img src="x.png">""", "An <img> has no alt attribute" },
        { "products", "control-names", """<svg aria-hidden="true" focusable="false" viewBox="0 0 1 1">""", """<svg viewBox="0 0 1 1">""", "An inline <svg> must be decorative" },
        { "products", "control-names", """<svg aria-hidden="true" focusable="false" viewBox="0 0 1 1">""", """<svg focusable="false" viewBox="0 0 1 1">""", "An inline <svg> must be decorative" },
        { "products", "control-names", """<svg aria-hidden="true" focusable="false" viewBox="0 0 1 1">""", """<svg aria-hidden="false" viewBox="0 0 1 1">""", "An inline <svg> must be decorative" },
        { "products", "control-names", "<footer>", """<footer><svg role="img"></svg>""", "An inline <svg> must be decorative" },
        { "products", "control-names", "<footer>", """<footer><svg><use href="/icons.svg#x"/></svg>""", "An inline <svg> must be decorative" },
        { "login-rejected", "live-regions", LoginAlert, """<div>Correo o contraseña incorrectos.</div>""", "The error state shows no visible role=\"alert\"" },
        { "login-rejected", "live-regions", LoginAlert, """<div aria-live="polite">Correo o contraseña incorrectos.</div>""", "The error state shows no visible role=\"alert\"" },
        { "login-rejected", "live-regions", LoginAlert, """<div role="alert" hidden>Correo o contraseña incorrectos.</div>""", "The error state shows no visible role=\"alert\"" },
        { "login-throttled", "live-regions", """<div role="alert">""", "<div>", "The error state shows no visible role=\"alert\"" },
        { "products-confirmed", "live-regions", """<div role="status" data-testid="notice-success">""", """<div data-testid="notice-success">""", "The success confirmation must be role=\"status\"" },
        { "products-confirmed", "live-regions", """<div role="status" data-testid="notice-success">""", """<div aria-live="off" data-testid="notice-success">""", "The success confirmation must be role=\"status\"" },
        { "products-confirmed", "live-regions", """<div role="status" data-testid="notice-success">""", """<div role="alert" data-testid="notice-success">""", "A confirmation page must not announce an error alert" },
        { "products-confirmed", "live-regions", """<div role="status" data-testid="notice-success">Producto creado.</div>""", """<div role="status">Cargando productos.</div>""", "No status element carries the confirmation message" },
        { "products-confirmed", "live-regions", """<div role="status" data-testid="notice-success">Producto creado.</div>""", """<div role="status">Buscando...</div><div data-testid="notice-success">Producto creado.</div>""", "The success confirmation must be role=\"status\"" },
        { "products-confirmed", "live-regions", """<div role="status" data-testid="notice-success">""", """<div role="status" data-testid="notice-success" hidden>""", "shows no visible role=\"status\"" },
        { "products-confirmed", "live-regions", """<div role="status" data-testid="notice-success">Producto creado.</div>""", """<div role="status" data-testid="notice-success"> </div>""", "shows no visible role=\"status\"" },
        { "login-rejected", "invalid-fields", EmailInvalidAttributes, ">", "The rejected field 'email' lacks aria-invalid" },
        { "login-rejected", "invalid-fields", EmailInvalidAttributes, """ aria-describedby="email-error">""", "The rejected field 'email' lacks aria-invalid" },
        { "login-rejected", "invalid-fields", EmailInvalidAttributes, """ aria-invalid="true">""", "has no aria-describedby pointing to an element that contains the message" },
        { "login-rejected", "invalid-fields", """aria-describedby="email-error">""", """aria-describedby="missing">""", "has no aria-describedby pointing to an element that contains the message" },
        { "login-rejected", "invalid-fields", EmailError, """<div id="email-error"></div>""", "has no aria-describedby pointing to an element that contains the message" },
        { "login-rejected", "invalid-fields", """aria-describedby="email-error">""", """aria-describedby="email-label">""", "points into a <label>" },
        { "login-rejected", "invalid-fields", EmailError, """<div id="email-error">Correo electrónico</div>""", "repeats the field's own label text" },
        { "login-rejected", "invalid-fields", """autocomplete="current-password">""", """autocomplete="current-password" aria-describedby="email-error">""", "also describes the valid field" },
        { "login-empty", "invalid-fields", """aria-invalid="true" aria-describedby="password-error">""", """aria-describedby="password-error">""", "The rejected field 'password' lacks aria-invalid" },
        { "products", "invalid-fields", """<input id="search" name="search" type="search">""", """<input id="search" name="search" type="search" aria-invalid="true">""", "A pristine form marks 'search' as invalid" },
        { "login-rejected", "input-hints", """autocomplete="current-password">""", """autocomplete="off">""", "must have autocomplete=\"current-password\"" },
        { "login-rejected", "input-hints", """autocomplete="username" """, "", "needs autocomplete=\"email\" or \"username\"" },
        { "login-rejected", "input-hints", """type="email" """, """type="text" """, "must be type=\"email\"" },
        { "login-rejected", "input-hints", PasswordInput, """<input id="password" name="Input.Secret" type="text">""", "found no password field on this Login page" },
        { "login-rejected", "input-hints", """name="Input.Email" """, """name="Input.Correo" """, "found no email field on this Login page" },
        { "products", "input-hints", """inputmode="decimal" """, "", "must have inputmode=\"decimal\"" },
        { "products", "input-hints", """inputmode="numeric" """, """inputmode="text" """, "must have inputmode=\"numeric\"" },
        { "products", "input-hints", """name="Create.Price" """, """name="Create.Importe" """, "found no 'price' field in a POST form on this Products page" },
        { "products", "input-hints", """name="Create.Stock" """, """name="Create.Existencias" """, "found no 'stock' field in a POST form on this Products page" },
        // 5. tables
        { "products", "tables", "<caption>Lista de productos</caption>", "", "has neither a <caption> nor an aria-label" },
        { "products", "tables", """<th scope="col">SKU</th>""", "<th>SKU</th>", "in the header must have scope=\"col\"" },
        { "products", "tables", Thead, "", "A data <table> has no column headers" },
        { "products", "tables", """<th scope="col" data-align="end">Precio</th>""", """<th scope="col">Precio</th>""", "The header of the price column must carry data-align=\"end\"" },
        { "products", "tables", """data-price="1.50" data-align="end">""", """data-price="1.50">""", "The price cell must carry data-align=\"end\"" },
        { "products", "tables", """data-stock="0" data-align="end">""", """data-stock="0">""", "The stock cell must carry data-align=\"end\"" },
        { "products", "tables", RegionOpen, "<div>", "needs a scroll region wrapper" },
        { "products", "tables", RegionOpen, """<div role="region" tabindex="0">""", "needs a scroll region wrapper" },
        { "products", "tables", RegionOpen, """<div role="region" aria-label="Lista de productos">""", "needs a scroll region wrapper" },
        { "products", "tables", "<tbody>", "<tbody><tr><th>Fila</th></tr>", "in the body must have scope=\"row\"" },
        { "products-table-with-cards", "tables", """<li data-testid="product-card">Llave</li>""", "", "needs a scroll region wrapper" },
        { "products-cards", "tables", """data-price="1.50" data-align="end">""", """data-price="1.50">""", "The price cell must carry data-align=\"end\"" },
        { "products-cards", "tables", """data-stock="6" data-align="end">""", """data-stock="6">""", "The stock cell must carry data-align=\"end\"" },
        { "products-cards", "tables", """<li data-testid="product-card"><div data-testid="product-row" data-product-id="33333333-3333-3333-3333-333333333333">""", """<li><div data-testid="product-row" data-product-id="33333333-3333-3333-3333-333333333333">""", "so each needs one data-testid=\"product-card\"" },
        { "home-owner", "tables", "<caption>Datos del espacio</caption>", "", "has neither a <caption> nor an aria-label" },
        { "home-owner", "tables", """<tr><th scope="row">Rol</th>""", """<tr><th>Rol</th>""", "in the body must have scope=\"row\"" },
        { "home-owner", "tables", """<tr><th scope="row">Rol</th><td>Propietario</td></tr>""", """<tr><td>Rol</td><td>Propietario</td></tr>""", "A data <table> has no column headers" },
        // 6. shell
        { "products", "logout", "</header>", """<form method="post" action="/logout"><button type="submit">Salir</button></form></header>""", "Expected exactly one logout form, found 2" },
        { "products", "logout", """<form method="post" action="/logout">""", """<form method="post" action="/salir">""", "Expected exactly one logout form, found 0" },
        { "error", "logout", "</header>", LogoutFormHtml + "</header>", "Expected no logout form, found 1" },
        { "products", "nav-duplicates", "</nav>", """<a href="/demo">Recorrido</a></nav>""", "The navigation repeats the link" },
        { "products", "menu-toggle", "<details>", "<div>", "must work without JavaScript" },
        { "products", "menu-toggle", "<summary>Menú</summary>", """<summary aria-expanded="false">Menú</summary>""", "must not carry aria-expanded" },
        { "products", "menu-toggle", "<summary>Menú</summary>", "<summary></summary>", "The menu <summary> has no accessible name" },
        { "products", "menu-toggle", "<summary>Menú</summary>", "", "has no <summary> toggle" },
        { "products-checkbox-menu", "menu-toggle", """<label for="menu-toggle">""", """<label for="otro">""", "must work without JavaScript" },
        { "products-checkbox-menu", "menu-toggle", """<input type="checkbox" id="menu-toggle">""", """<input type="checkbox">""", "must work without JavaScript" },
        { "products-checkbox-menu", "menu-toggle", """<label for="menu-toggle">Menú</label>""", """<label for="menu-toggle"></label>""", "need an accessible name" },
        { "products-checkbox-menu", "menu-toggle", """<label for="menu-toggle">Menú</label>""", "", "must work without JavaScript" },
        { "products", "inline-styles", "<footer>", """<footer style="color:red">""", "Inline style attribute" },
        { "products", "inline-styles", """<svg aria-hidden="true" focusable="false" viewBox="0 0 1 1">""", """<svg aria-hidden="true" focusable="false" style="width:1px">""", "Inline style attribute" },
        { "products", "scripts", "</head>", "<script>var a = 1;</script></head>", "At most one inline script" },
        { "products", "scripts", "</head>", """<script src="https://cdn.example.test/x.js"></script></head>""", "A script is loaded from another origin" },
        { "products", "scripts", "try{var t", "/*" + new string('x', 700) + "*/try{var t", "the theme bootstrap must stay under 600" },
        { "products", "scripts", "<script>try", """<script type="text/javascript">try""", "must have no type attribute" },
        // 7. theme toggle
        { "products", "theme-toggle", ThemeButton, ThemeButton.Replace(" type=\"button\"", string.Empty, StringComparison.Ordinal), "must be type=\"button\"" },
        { "products", "theme-toggle", ThemeButton, ThemeButton.Replace("type=\"button\"", "type=\"submit\"", StringComparison.Ordinal), "must be type=\"button\"" },
        { "products", "theme-toggle", ThemeButton, ThemeButton.Replace(" aria-label=\"Cambiar tema\"", string.Empty, StringComparison.Ordinal), "name must contain \"tema\" or \"modo\"" },
        { "products", "theme-toggle", ThemeButton, ThemeButton.Replace("Cambiar tema", "Alternar", StringComparison.Ordinal), "name must contain \"tema\" or \"modo\"" },
        { "products", "theme-toggle", ThemeButton, ThemeButton.Replace(" data-theme-toggle", string.Empty, StringComparison.Ordinal), "The shell has no button[data-theme-toggle]" },
        { "products", "theme-toggle", ThemeButton, """<a href="#" data-theme-toggle aria-label="Cambiar tema">Tema</a>""", "data-theme-toggle must be on a <button>" },
        { "products", "theme-toggle", ThemeButton, """<div role="button" tabindex="0" data-theme-toggle aria-label="Cambiar tema">Tema</div>""", "data-theme-toggle must be on a <button>" },
        { "products", "theme-toggle", ThemeButton, $"""<form method="post" action="/x">{ThemeButton}</form>""", "must not sit inside a form" },
        { "login-rejected", "theme-toggle", " data-theme-toggle", "", "The shell has no button[data-theme-toggle]" },
        { "login-rejected", "theme-toggle", """<button type="button" data-theme-toggle>""", """<form method="post" action="/login"><button type="button" data-theme-toggle>""", "must not sit inside a form" },
        // 8. error answers
        { "invalid-request", "rejected-request", InvalidRequestMessage, "<p>Página no encontrada.</p>", "must not say that something was not found" },
        { "invalid-request", "rejected-request", InvalidRequestMessage, "<p>Algo salió mal.</p>", "must tell, in Spanish, that the request is invalid or has expired" },
        { "invalid-request", "rejected-request", InvalidRequestMessage, "<p>The request is invalid or has expired.</p>", "must tell, in Spanish, that the request is invalid or has expired" },
        { "invalid-request", "rejected-request", """<div data-page="error">""", """<div data-page="not-found">""", "exactly one data-page=\"error\" region, found 0" },
        { "invalid-request", "rejected-request", InvalidRequestHeading, InvalidRequestHeading + "<h1>Otra vez</h1>", "needs exactly one visible h1, found 2" },
        { "invalid-request-signed-in", "rejected-request", InvalidRequestHeading, "", "needs exactly one visible h1, found 0" },
        // in-page links: resolved against <base href="/">, a bare #fragment leaves the page
        { "products", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="#sku">Ir al SKU</a>""", "In-page link <a href=\"#sku\">Ir al SKU</a>: its href \"#sku\" resolves (against https://localhost/) to https://localhost/#sku, which leaves the page: an in-page link must resolve to /products#sku" },
        { "products", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="/#sku">Ir al SKU</a>""", "which leaves the page: an in-page link must resolve to /products#sku" },
        { "products", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="#">Arriba</a>""", "which leaves the page" },
        { "products", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="/products?page=2#sku">Ir al SKU</a>""", "which leaves the page: an in-page link must resolve to /products#sku" },
        { "products", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="/products#no-existe">Ir a ninguna parte</a>""", "targets #no-existe, but no element of the page has that id" },
        { "products", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="/products">Aquí</a><map><area href="#sku" alt="SKU"></map>""", "In-page link <area href=\"#sku\" alt=\"SKU\">" },
        { "products-page-two", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="/products#sku">Ir al SKU</a>""", "an in-page link must resolve to /products?page=2&search=ab#sku" },
        { "products-page-two", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="#sku">Ir al SKU</a>""", "an in-page link must resolve to /products?page=2&search=ab#sku" },
        { "products-empty", "in-page-links", """<a href="/products#sku">""", """<a href="#sku">""", "which leaves the page: an in-page link must resolve to /products#sku" },
        // 9. content hooks
        { "products", "page-region", """data-page="products">""", """data-page="not-found">""", "but this is a \"products\" page" },
        { "products", "page-region", """data-page="products">""", """data-page="forbidden">""", "but this is a \"products\" page" },
        { "products", "page-region", """<div data-page="products"><h1>Productos</h1>""", """<h1>Productos</h1><div data-page="products">""", "must contain the page's <h1>" },
        { "products", "page-region", """<div data-page="products">""", "<div>", "Expected exactly one element data-page=\"products\"" },
        { "products", "page-region", """<main id="contenido"><div data-page="products">""", """<div data-page="products"><main id="contenido">""", "must be the <main> or inside it" },
        { "login-rejected", "page-region", """data-page="login">""", """data-page="register">""", "but this is a \"login\" page" },
        { "forbidden-users", "page-region", """data-page="forbidden">""", """data-page="not-found">""", "but this is a \"forbidden\" page" },
        { "not-found", "page-region", """data-page="not-found">""", """data-page="forbidden">""", "but this is a \"not-found\" page" },
        { "error", "page-region", """data-page="error">""", """data-page="not-found">""", "but this is a \"error\" page" },
        { "invalid-request", "page-region", """data-page="error">""", """data-page="not-found">""", "but this is a \"error\" page" },
        { "products-empty", "empty-state", "data-testid=\"empty-state\"", "data-testid=\"empty\"", "needs exactly one data-testid=\"empty-state\", found 0" },
        { "products-empty", "empty-state", "<p>Todavía no hay productos.</p>", "<p> </p>", "The empty state has no message" },
        { "products-empty", "empty-state", """<a href="/products#sku">Agregar el primer producto</a>""", "", "needs a call to action" },
        { "products-empty", "empty-state", "<p>Todavía no hay productos.</p>", "<p>No products found.</p>", "The empty state message is not Spanish" },
        { "products", "empty-state", "</main>", "<div data-testid=\"empty-state\"><p>Nada.</p></div></main>", "must not show data-testid=\"empty-state\"" },
        { "products", "stock-status", """<span data-stock-status="out">""", """<span data-stock-status="low">""", "must carry exactly one data-stock-status=\"out\"" },
        { "products", "stock-status", """<span data-stock-status="low">""", """<span data-stock-status="ok">""", "must carry exactly one data-stock-status=\"low\"" },
        { "products", "stock-status", """<span data-stock-status="ok">""", """<span data-stock-status="low">""", "must carry exactly one data-stock-status=\"ok\"" },
        { "products", "stock-status", """<span data-stock-status="out">Agotado</span>""", "Agotado", "must carry exactly one data-stock-status=\"out\"" },
        { "products", "stock-status", """<span data-stock-status="out">Agotado</span>""", """<span data-stock-status="out"></span>""", "has no visible text" },
        { "products-cards", "stock-status", """<span data-stock-status="ok">Disponible</span>""", """<span data-stock-status="low">Disponible</span>""", "must carry exactly one data-stock-status=\"ok\"" },
        { "home-owner", "chips", """<span data-chip="owner">""", """<span data-chip="miembro">""", "Expected one data-chip=\"owner\" for an owner" },
        { "home-owner", "chips", """<span data-chip="permission">""", """<span data-chip="otro">""", "Every held permission needs a data-chip=\"permission\"" },
        { "home-owner", "chips", """<span data-chip="permission">Ver productos</span>""", """<span data-chip="permission">Otra cosa</span>""", "chip showing \"Ver productos\"" },
        { "home-owner", "chips", """data-value="true">""", """data-value="false">""", "Expected no data-chip=\"owner\" for a member" },
    };

    [Theory]
    [MemberData(nameof(Mutations))]
    public void A_broken_page_is_reported_by_the_rule_that_owns_the_defect(string fixture, string rule, string from, string to, string expected)
    {
        var broken = Broken(fixture, (from, to));

        AssertReports(Problems(rule, CaseOf(fixture, broken)), expected);
    }

    [Fact]
    public void A_duplicate_id_is_the_only_problem_when_every_control_stays_labelled()
    {
        var broken = Broken("products", ("<p>Conversia</p>", """<p id="sku">Conversia</p>"""));

        var problems = Problems("control-names", CaseOf("products", broken));

        Assert.Equal("[products] The id \"sku\" is used more than once.", Assert.Single(problems));
    }

    // ---- modern markup that must NOT be reported ----

    private const string ThemeColors = """<meta name="theme-color" content="#ffffff" media="(prefers-color-scheme: light)"><meta name="theme-color" content="#111111" media="(prefers-color-scheme: dark)">""";

    public static TheoryData<string, string, string, string> Accepted => new()
    {
        { "products", "document", """<meta name="color-scheme" content="light dark">""", ThemeColors },
        { "products", "document", """<meta name="color-scheme" content="light dark">""", """<meta name="color-scheme" content="dark light">""" },
        { "products", "document", """<meta name="color-scheme" content="light dark">""", """<meta name="color-scheme" content="only light"><meta name="theme-color" content="#fff" media="(prefers-color-scheme:light)"><meta name="theme-color" content="#000" media="(prefers-color-scheme:dark)">""" },
        { "products", "document", """href="icon.svg">""", """href="data:image/png;base64,iVBORw0KGgo=">""" },
        { "products", "document", """href="icon.svg">""", """href="data:image/svg+xml,%3Csvg%2F%3E">""" },
        { "products", "document", """<link rel="icon" type="image/svg+xml" href="icon.svg">""", """<link rel="shortcut icon" href="icon.svg"><link rel="icon" href="data:image/png;base64,AAAA">""" },
        { "products", "landmarks", SkipLink, """<div tabindex="-1">Inicio del documento</div><a href="/products#contenido">Saltar al contenido</a>""" },
        { "products", "landmarks", SkipLink, """<input type="hidden" name="x" value="1"><div hidden><a href="/x">Oculto</a></div><a href="/products#contenido">Saltar al contenido</a>""" },
        { "products", "landmarks", SkipLink, """<a href="https://localhost/products#contenido">Saltar al contenido</a>""" },
        { "products", "landmarks", """<base href="/">""", "" },
        { "products", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="/products#sku">Ir al SKU</a>""" },
        { "products", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="https://localhost/products#sku">Ir al SKU</a>""" },
        { "products", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="/demo#caracteristicas">Ver más</a><a href="https://example.test/#top">Fuera</a><a href="mailto:a@example.test#x">Correo</a>""" },
        { "products", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="/products">Sin fragmento</a>""" },
        { "products-page-two", "in-page-links", "<h2>Agregar un producto</h2>", """<h2>Agregar un producto</h2><a href="/products?page=2&search=ab#sku">Ir al SKU</a>""" },
        { "products", "headings", "<h1>Productos</h1>", "<h2>Barra lateral</h2><h1>Productos</h1>" },
        { "products", "headings", "<h1>Productos</h1>", "<h3>Barra lateral</h3><h1>Productos</h1>" },
        { "products", "headings", "<h2>Agregar un producto</h2>", "<h2>Agregar un producto</h2><h3>Detalle</h3>" },
        { "products", "headings", "<h2>Agregar un producto</h2>", "<h2>Agregar un producto</h2><h3>Detalle</h3><h2>Otra sección</h2>" },
        { "products", "control-names", """<svg aria-hidden="true" focusable="false" viewBox="0 0 1 1">""", """<svg aria-hidden="true" viewBox="0 0 1 1">""" },
        { "products", "control-names", "<footer>", """<footer><svg aria-hidden="true"><use href="/icons.svg#x"/></svg>""" },
        { "products", "control-names", "<footer>", """<footer><div aria-hidden="true"><svg viewBox="0 0 1 1"></svg></div>""" },
        { "products", "control-names", "<footer>", """<footer><svg role="img" aria-label="Logotipo"></svg>""" },
        { "products", "control-names", "<footer>", """<footer><svg role="img"><title>Logotipo</title></svg>""" },
        { "login-rejected", "live-regions", LoginAlert, """<div aria-live="assertive">Correo o contraseña incorrectos.</div>""" },
        { "login-rejected", "live-regions", LoginAlert, """<p role="alert">Correo o contraseña incorrectos.</p>""" },
        { "products-confirmed", "live-regions", """<div role="status" data-testid="notice-success">""", """<div aria-live="polite" data-testid="notice-success">""" },
        { "products-confirmed", "live-regions", """<div role="status" data-testid="notice-success">Producto creado.</div>""", """<div role="status">Producto guardado.</div>""" },
        { "products-confirmed", "live-regions", """<div role="status" data-testid="notice-success">Producto creado.</div>""", """<div aria-live="polite">Permiso concedido.</div>""" },
        { "products-confirmed", "live-regions", """<div role="status" data-testid="notice-success">Producto creado.</div>""", """<div role="status"><p data-testid="notice-success">Hecho.</p></div>""" },
        { "login-rejected", "invalid-fields", """aria-describedby="email-error">""", """aria-describedby="email-label email-error">""" },
        { "login-empty", "invalid-fields", """aria-describedby="password-error">""", """aria-describedby="email-error">""" },
        { "products", "input-hints", CreateForm, "" },
        { "products", "tables", Thead, Thead.Replace("<thead>", "<thead class=\"sticky\">", StringComparison.Ordinal) },
        { "products", "tables", RegionOpen + TableHtml + "</div>", """<table><caption>Una fila</caption><tbody><tr><td>a</td><td>b</td></tr></tbody></table>""" },
        { "home-owner", "tables", KeyValueTable, """<table><caption>Una fila</caption><tbody><tr><td>a</td><td>b</td></tr></tbody></table>""" },
        { "home-owner", "tables", KeyValueTable, """<table aria-label="Pares"><tr><th scope="row">A</th><td>1</td></tr></table>""" },
        { "home-owner", "tables", KeyValueTable, """<table><caption>Con encabezado</caption><tbody><tr><th scope="col">A</th><th scope="col">B</th></tr><tr><td>1</td><td>2</td></tr><tr><td>3</td><td>4</td></tr></tbody></table>""" },
        { "products", "theme-toggle", """aria-label="Cambiar tema">""", """aria-label="Alternar modo claro u oscuro">""" },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Legitimate_modern_markup_is_not_reported(string fixture, string rule, string from, string to)
    {
        AssertNoProblems(Problems(rule, CaseOf(fixture, Broken(fixture, (from, to)))));
    }

    // ---- in-page links are judged after resolving them against the document's base URL ----

    private const string NoBase = "";

    private static UiPageCase LinkPage(string path, string baseElement, string links, UiPageType type = UiPageType.Register)
    {
        var html = $"""
            <!DOCTYPE html><html lang="es"><head>{baseElement}<title>Página</title></head><body>
            <main id="main"><div data-page="{UiPageTypes.DataPage(type)}"><h1>Página</h1><h2 id="seccion">Sección</h2>{links}</div></main>
            </body></html>
            """;
        return new UiPageCase { Label = path, Page = Respond(path, HttpStatusCode.OK, html, "text/html; charset=utf-8"), Type = type };
    }

    public static TheoryData<string, string, string> CompliantInPageLinks => new()
    {
        { "/register", """<base href="/">""", """<a href="/register#main">Saltar</a>""" },
        { "/register", """<base href="/">""", """<a href="/register#main">Saltar</a><a href="/register#seccion">Sección</a>""" },
        { "/demo", """<base href="/">""", """<a href="/demo#main">Saltar</a>""" },
        { "/", """<base href="/">""", """<a href="#main">Saltar</a><a href="/#seccion">Sección</a>""" },
        { "/products?page=2&search=ab", """<base href="/">""", """<a href="/products?page=2&search=ab#main">Saltar</a>""" },
        { "/products?page=2&search=ab", """<base href="/">""", """<a href="/products?page=2&amp;search=ab#seccion">Sección</a>""" },
        { "/register", NoBase, """<a href="#main">Saltar</a>""" },
        { "/products?page=2&search=ab", NoBase, """<a href="#main">Saltar</a><a href="?page=2&amp;search=ab#seccion">Sección</a>""" },
        { "/register", """<base href="/register">""", """<a href="#main">Saltar</a>""" },
        { "/register", """<base href="/">""", """<a href="https://example.test/#main">Fuera</a><a href="https://example.test/register#main">Fuera</a><a href="/demo#caracteristicas">Otra página</a><a href="/login#main-form">Otra página</a><a href="/icons.svg#x">Sprite</a><a href="mailto:a@example.test#main">Correo</a><a href="/register">Sin fragmento</a>""" },
    };

    [Theory]
    [MemberData(nameof(CompliantInPageLinks))]
    public void An_in_page_link_that_resolves_to_the_page_being_viewed_plus_a_fragment_of_the_page_is_accepted(string path, string baseElement, string links)
        => AssertNoProblems(UiStructureAssertions.InPageLinks(LinkPage(path, baseElement, links)).ToList());

    public static TheoryData<string, string, string, string> ViolatingInPageLinks => new()
    {
        // the real defect: the bare fragment resolves against <base href="/"> and leaves for the home page
        { "/register", """<base href="/">""", """<a href="#main">Saltar</a>""", "resolves (against https://localhost/) to https://localhost/#main, which leaves the page: an in-page link must resolve to /register#main" },
        { "/demo", """<base href="/">""", """<a href="#main">Saltar</a>""", "an in-page link must resolve to /demo#main" },
        { "/register", """<base href="/">""", """<a href="/#main">Saltar</a>""", "resolves (against https://localhost/) to https://localhost/#main, which leaves the page: an in-page link must resolve to /register#main" },
        { "/products", """<base href="/">""", """<a href="#seccion">Sección</a>""", "an in-page link must resolve to /products#seccion" },
        { "/products?page=2&search=ab", """<base href="/">""", """<a href="/products#main">Saltar</a>""", "an in-page link must resolve to /products?page=2&search=ab#main" },
        { "/products?page=2&search=ab", """<base href="/">""", """<a href="/products?page=2#main">Saltar</a>""", "an in-page link must resolve to /products?page=2&search=ab#main" },
        { "/products?page=2&search=ab", """<base href="/">""", """<a href="#main">Saltar</a>""", "an in-page link must resolve to /products?page=2&search=ab#main" },
        { "/products?page=2&search=ab", """<base href="/products">""", """<a href="#main">Saltar</a>""", "an in-page link must resolve to /products?page=2&search=ab#main" },
        { "/register", """<base href="/">""", """<a href="/register#no-existe">Saltar</a>""", "targets #no-existe, but no element of the page has that id" },
        { "/register", NoBase, """<a href="#no-existe">Saltar</a>""", "targets #no-existe, but no element of the page has that id" },
        { "/register", NoBase, """<a href="#">Arriba</a>""", "has an empty fragment" },
        { "/register", """<base href="/">""", """<a href="/register#">Arriba</a>""", "has an empty fragment" },
    };

    [Theory]
    [MemberData(nameof(ViolatingInPageLinks))]
    public void An_in_page_link_that_leaves_the_page_or_misses_its_target_is_reported(string path, string baseElement, string links, string expected)
    {
        var problems = UiStructureAssertions.InPageLinks(LinkPage(path, baseElement, links)).ToList();

        AssertReports(problems, expected);
        Assert.Single(problems);
    }

    [Fact]
    public void Every_in_page_link_of_a_page_is_judged_not_just_the_first()
    {
        var page = LinkPage("/register", """<base href="/">""", """<a href="/register#main">Bien</a><a href="#main">Mal</a><a href="/#seccion">Mal</a>""");

        var problems = UiStructureAssertions.InPageLinks(page).ToList();

        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.StartsWith("[/register] In-page link", p));
    }

    // ---- defects that take more than one change ----

    private sealed record Defect(string Fixture, string Rule, string Expected, (string From, string To)[] Changes);

    private static readonly IReadOnlyDictionary<string, Defect> Defects = new Dictionary<string, Defect>
    {
        ["the error message sits inside the field's own label"] = new("login-rejected", "invalid-fields", "points into a <label>",
            [("""<label for="email" id="email-label">Correo electrónico</label>""", """<label for="email" id="email-label">Correo electrónico <span id="in-label">Introduce un correo válido.</span></label>"""),
             ("""aria-describedby="email-error">""", """aria-describedby="in-label">""")]),
        ["a summary shared by the invalid field and a valid one"] = new("login-rejected", "invalid-fields", "also describes the valid field",
            [(PasswordInput, """<input id="password" name="Input.Password" type="password" autocomplete="current-password" aria-describedby="email-error">""")]),
        ["the favicon.png of the template renamed"] = new("products", "document", "default favicon.png",
            [("""href="icon.svg">""", """href="brand.png">""")]),
        ["a 400 page that says the page was not found"] = new("invalid-request", "rejected-request", "no encontrad",
            [(InvalidRequestMessage, "<p>La página no encontrada. La solicitud no es válida.</p>")]),
    };

    public static TheoryData<string> DefectNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Defects.Keys)
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DefectNames))]
    public void A_defect_made_of_several_changes_is_reported_by_its_rule(string name)
    {
        var defect = Defects[name];
        var assets = Assets(tweak: files => files[Url("/brand.png")] = Asset("/brand.png", DefaultFavicon.Bytes, "image/png"));

        AssertReports(Problems(defect.Rule, CaseOf(defect.Fixture, Broken(defect.Fixture, defect.Changes)), assets), defect.Expected);
    }

    // ---- the favicon is judged on the bytes served ----

    [Fact]
    public void A_renamed_copy_of_the_default_favicon_is_still_the_default_and_nothing_else_is_reported()
    {
        var assets = Assets(tweak: files => files[Url("/logo.png")] = Asset("/logo.png", DefaultFavicon.Bytes, "image/png"));

        var problems = Problems("document", CaseOf("products", Broken("products", ("""href="icon.svg">""", """href="logo.png">"""))), assets);

        Assert.Single(problems);
        AssertReports(problems, "default favicon.png");
    }

    [Fact]
    public void The_favicon_png_name_alone_is_not_a_defect_when_the_bytes_are_a_new_icon()
    {
        byte[] newIcon = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
        var assets = Assets(tweak: files => files[Url("/favicon.png")] = Asset("/favicon.png", newIcon, "image/png"));

        var problems = Problems("document", CaseOf("products", Broken("products", ("""href="icon.svg">""", """href="favicon.png">"""))), assets);

        AssertNoProblems(problems);
    }

    [Fact]
    public void The_default_favicon_under_the_default_name_is_the_only_problem_when_it_resolves()
    {
        var problems = Problems("document", CaseOf("products", Broken("products", ("""href="icon.svg">""", """href="favicon.png">"""))));

        Assert.Single(problems);
        AssertReports(problems, "default favicon.png");
    }

    [Fact]
    public void An_icon_that_is_not_served_as_an_image_is_reported()
    {
        var assets = Assets(tweak: files => files[Url("/icon.svg")] = Asset("/icon.svg", HttpStatusCode.OK, "<svg/>", "text/html", true));

        AssertReports(Problems("document", CaseOf("products"), assets), "not image/*");
    }

    [Fact]
    public void A_missing_icon_file_is_reported()
    {
        AssertReports(Problems("document", CaseOf("products"), Assets(icon: false)), "does not resolve with 200");
    }

    // ---- stylesheets served by the site ----

    [Fact]
    public void A_bootstrap_reference_in_the_page_is_reported()
    {
        var broken = Broken("products", ("<link rel=\"stylesheet\" href=\"app.css\">", "<link rel=\"stylesheet\" href=\"app.css\"><link rel=\"stylesheet\" href=\"lib/bootstrap/dist/css/bootstrap.min.css\">"));

        AssertReports(Problems("stylesheet-links", CaseOf("products", broken)), "The page still references /lib/bootstrap.");
    }

    [Fact]
    public void A_stylesheet_that_is_missing_not_css_uncacheable_or_foreign_is_reported()
    {
        AssertReports(Problems("stylesheet-links", CaseOf("products"), Assets(cssStatus: HttpStatusCode.NotFound)), "does not respond 200");
        AssertReports(Problems("stylesheet-links", CaseOf("products"), Assets(cssType: "text/html")), "not text/css");
        AssertReports(Problems("stylesheet-links", CaseOf("products"), Assets(cssCacheable: false)), "has neither Cache-Control nor ETag");
        AssertReports(Problems("stylesheet-links", CaseOf("products"), new UiAssets(new Dictionary<Uri, UiAsset>())), "does not respond 200");

        var foreign = Broken("products", ("href=\"app.css\"", "href=\"https://cdn.example.test/app.css\""));
        AssertReports(Problems("stylesheet-links", CaseOf("products", foreign)), "is not served by this site");
    }

    [Fact]
    public void A_page_without_any_stylesheet_is_reported()
    {
        var bare = Broken("products", ("<link rel=\"stylesheet\" href=\"app.css\">", string.Empty));

        AssertReports(Problems("stylesheet-links", CaseOf("products", bare)), "The page links no stylesheet.");
    }

    // ---- the css content ----

    private const string DarkMedia = "@media (prefers-color-scheme: dark){:root{--c1:0}}";
    private const string MotionMedia = "@media (prefers-reduced-motion: reduce){*{animation:none}}";
    private const string Focus = ":focus-visible{outline:2px solid}";

    private static string Design(string root, string dark = DarkMedia, string motion = MotionMedia, string focus = Focus)
        => string.Join('\n', root.Replace("TOKENS", Tokens, StringComparison.Ordinal), dark, motion, focus);

    public static TheoryData<string, string> CssMutations => new()
    {
        { "design", "prefers-color-scheme: dark" },
        { "design", "prefers-reduced-motion" },
        { "design", ":focus-visible" },
        { "design", "--c10:10" },
        { "theme", """[data-theme="dark"]""" },
        { "theme", """[data-theme="light"]""" },
    };

    [Theory]
    [MemberData(nameof(CssMutations))]
    public void A_stylesheet_missing_a_design_system_feature_is_reported(string group, string removed)
    {
        Assert.Contains(removed, Css);
        var broken = group == "theme"
            ? Css.Replace(removed, "[data-x=\"y\"]", StringComparison.Ordinal)
            : Css.Replace(removed, removed.StartsWith("--", StringComparison.Ordinal) ? string.Empty : "nada", StringComparison.Ordinal);

        var problems = (group == "theme" ? UiStructureAssertions.ThemeOverridesCss(broken) : UiStructureAssertions.DesignSystemCss(broken)).ToList();

        AssertReports(problems, removed switch
        {
            "prefers-color-scheme: dark" => "no @media (prefers-color-scheme: dark)",
            "prefers-reduced-motion" => "no @media (prefers-reduced-motion",
            ":focus-visible" => "no :focus-visible rule",
            "--c10:10" => "at least 10 custom properties",
            _ => $"no {removed} override selector",
        });
    }

    public static TheoryData<string, string> AcceptedCss => new()
    {
        { "dark scheme under a media type", Design(":root{TOKENS}", dark: "@media screen and (prefers-color-scheme: dark){:root{--c1:0}}") },
        { "dark scheme combined with a width query", Design(":root{TOKENS}", dark: "@media (prefers-color-scheme: dark) and (min-width: 600px){:root{--c1:0}}") },
        { "dark scheme minified", Design(":root{TOKENS}", dark: "@media(prefers-color-scheme:dark){:root{--c1:0}}") },
        { "reduced motion under a media type", Design(":root{TOKENS}", motion: "@media screen and (prefers-reduced-motion: reduce){*{animation:none}}") },
        { "reduced motion no-preference", Design(":root{TOKENS}", motion: "@media (prefers-reduced-motion: no-preference){*{scroll-behavior:smooth}}") },
        { "reduced motion combined with another query", Design(":root{TOKENS}", motion: "@media (prefers-reduced-motion: reduce) and (min-width: 1px){*{animation:none}}") },
        { "root in a selector list with a theme selector", Design(":root,[data-theme=light]{TOKENS}") },
        { "root in a selector list with host", Design(":root, :host{TOKENS}") },
        { "root spread over lines in a list", Design(":root,\n:host,\n[data-theme=\"light\"]\n{\n  TOKENS\n}") },
        { "root inside a layer", Design("@layer base{:root{TOKENS}}") },
        { "tokens split over two root blocks", Design(":root{--c1:1;--c2:2;--c3:3;--c4:4;--c5:5} :root{--c6:6;--c7:7;--c8:8;--c9:9;--c10:10}") },
        { "a comment with braces before the root", Design("/* a { b } */ :root{TOKENS}") },
    };

    [Theory]
    [MemberData(nameof(AcceptedCss))]
    public void Legitimate_modern_css_satisfies_the_design_system_rule(string description, string css)
    {
        Assert.NotEmpty(description);
        AssertNoProblems(UiStructureAssertions.DesignSystemCss(css).ToList());
    }

    public static TheoryData<string, string, string> RejectedCss => new()
    {
        { "dark scheme only in a comment", Design(":root{TOKENS}", dark: "/* @media (prefers-color-scheme: dark){} */"), "no @media (prefers-color-scheme: dark)" },
        { "light scheme media", Design(":root{TOKENS}", dark: "@media (prefers-color-scheme: light){:root{--c1:0}}"), "no @media (prefers-color-scheme: dark)" },
        { "dark scheme named in a declaration", Design(":root{TOKENS}", dark: ".x{content:'prefers-color-scheme: dark'}"), "no @media (prefers-color-scheme: dark)" },
        { "reduced motion in a declaration", Design(":root{TOKENS}", motion: ".x{content:'prefers-reduced-motion: reduce'}"), "no @media (prefers-reduced-motion" },
        { "tokens only on a themed root", Design(":root[data-theme=\"dark\"]{TOKENS}"), "at least 10 custom properties" },
        { "tokens only on a root with a pseudo-class", Design(":root:not([data-theme=\"light\"]){TOKENS}"), "at least 10 custom properties" },
        { "tokens on html:root", Design("html:root{TOKENS}"), "at least 10 custom properties" },
        { "tokens on a class", Design(".card{TOKENS}"), "at least 10 custom properties" },
        { "nine tokens", Design(":root,:host{--c1:1;--c2:2;--c3:3;--c4:4;--c5:5;--c6:6;--c7:7;--c8:8;--c9:9}"), "it declares 9" },
        { "the same token repeated", Design(":root{--c1:1} :root{--c1:2;--c2:2;--c3:3;--c4:4;--c5:5;--c6:6;--c7:7;--c8:8;--c9:9}"), "it declares 9" },
    };

    [Theory]
    [MemberData(nameof(RejectedCss))]
    public void Css_that_only_looks_like_the_design_system_is_reported(string description, string css, string expected)
    {
        Assert.NotEmpty(description);
        AssertReports(UiStructureAssertions.DesignSystemCss(css).ToList(), expected);
    }

    [Fact]
    public void Theme_overrides_are_found_in_any_selector_shape_and_missing_ones_are_reported()
    {
        Assert.Empty(UiStructureAssertions.ThemeOverridesCss(":root,[data-theme=light]{--a:1} :root[data-theme=dark]{--a:2}"));
        Assert.Empty(UiStructureAssertions.ThemeOverridesCss("[ data-theme = 'light' ]{} html[data-theme='dark'] body{}"));
        AssertReports(UiStructureAssertions.ThemeOverridesCss("/* [data-theme=light] */ [data-theme=dark]{}").ToList(), "no [data-theme=\"light\"] override selector");
    }

    [Fact]
    public void Empty_css_is_reported()
    {
        AssertReports(UiStructureAssertions.DesignSystemCss(string.Empty).ToList(), "There is no CSS to inspect.");
        AssertReports(UiStructureAssertions.ThemeOverridesCss(string.Empty).ToList(), "There is no CSS to inspect.");
    }

    // ---- the css of a page: linked sheets, their imports and inline styles; per page, never all pages together ----

    private const string ImportingCss = """
        @import url("tokens.css");
        @import "/dark.css" screen;
        :root[data-theme="dark"]{--c1:0}
        :root[data-theme="light"]{--c1:1}
        :focus-visible{outline:2px solid}
        """;

    private static UiAssets AssetsWithImports(bool withTokens = true) => Assets(css: ImportingCss, tweak: files =>
    {
        if (withTokens)
        {
            files[Url("/tokens.css")] = Asset("/tokens.css", HttpStatusCode.OK, ":root, :host{" + Tokens + "}", "text/css", true);
        }

        files[Url("/dark.css")] = Asset("/dark.css", HttpStatusCode.OK, DarkMedia, "text/css", true);
    });

    private const string InlineStyle = "<style>@media (prefers-reduced-motion: reduce){*{animation:none}}</style>";

    [Fact]
    public void A_design_system_spread_over_imports_and_an_inline_style_is_complete_for_that_page()
    {
        var page = CaseOf("products", Broken("products", ("</head>", InlineStyle + "</head>")));
        var assets = AssetsWithImports();

        AssertNoProblems(UiStructureAssertions.DesignSystemCss(assets.CssOf(page.Page)).ToList());
        AssertNoProblems(UiStructureAssertions.ThemeOverridesCss(assets.CssOf(page.Page)).ToList());
        AssertNoProblems(Problems("stylesheet-links", page, assets));
    }

    [Fact]
    public void Without_the_inline_style_the_page_lacks_reduced_motion()
    {
        var assets = AssetsWithImports();

        AssertReports(UiStructureAssertions.DesignSystemCss(assets.CssOf(CaseOf("products").Page)).ToList(), "no @media (prefers-reduced-motion");
    }

    [Fact]
    public void An_import_that_cannot_be_fetched_is_reported_and_its_rules_do_not_count()
    {
        var page = CaseOf("products", Broken("products", ("</head>", InlineStyle + "</head>")));
        var assets = AssetsWithImports(withTokens: false);

        AssertReports(Problems("stylesheet-links", page, assets), "The @import of /tokens.css (from /app.css) does not respond 200");
        AssertReports(UiStructureAssertions.DesignSystemCss(assets.CssOf(page.Page)).ToList(), "at least 10 custom properties");
    }

    [Fact]
    public void An_import_that_is_not_css_or_comes_from_inline_style_is_checked_too()
    {
        var html = Broken("products", ("</head>", "<style>@import url('/inline.css');</style></head>"));
        var assets = Assets(tweak: files => files[Url("/inline.css")] = Asset("/inline.css", HttpStatusCode.OK, "a{}", "text/html", true));

        AssertReports(Problems("stylesheet-links", CaseOf("products", html), assets), "The imported stylesheet /inline.css is served as");
        Assert.Contains(UiStructureAssertions.LinkedFiles(CaseOf("products", html).Page), u => u.AbsolutePath == "/inline.css");
    }

    [Fact]
    public void Imports_that_loop_or_leave_the_site_do_not_hang_nor_count()
    {
        var assets = Assets(css: "@import url('loop.css'); @import url(https://cdn.example.test/x.css); " + Css, tweak: files =>
            files[Url("/loop.css")] = Asset("/loop.css", HttpStatusCode.OK, "@import 'app.css';", "text/css", true));
        var page = CaseOf("products");

        AssertNoProblems(UiStructureAssertions.DesignSystemCss(assets.CssOf(page.Page)).ToList());
        AssertNoProblems(Problems("stylesheet-links", page, assets));
    }

    [Fact]
    public void Each_page_is_judged_on_its_own_css_and_not_on_the_css_of_all_pages_together()
    {
        const string withoutDark = ":root{" + Tokens + "} :root[data-theme=\"dark\"]{--c1:0} :root[data-theme=\"light\"]{--c1:1} " + MotionMedia + " " + Focus;
        var assets = Assets(tweak: files =>
        {
            files[Url("/part1.css")] = Asset("/part1.css", HttpStatusCode.OK, withoutDark, "text/css", true);
            files[Url("/part2.css")] = Asset("/part2.css", HttpStatusCode.OK, DarkMedia, "text/css", true);
        });
        var first = CaseOf("products", Broken("products", ("href=\"app.css\"", "href=\"part1.css\"")));
        var second = CaseOf("products-empty", Broken("products-empty", ("href=\"app.css\"", "href=\"part2.css\"")));

        AssertReports(UiStructureAssertions.DesignSystemCss(assets.CssOf(first.Page)).ToList(), "no @media (prefers-color-scheme: dark)");
        AssertReports(UiStructureAssertions.DesignSystemCss(assets.CssOf(second.Page)).ToList(), "at least 10 custom properties");
        AssertNoProblems(UiStructureAssertions.DesignSystemCss(assets.CssOf(first.Page) + assets.CssOf(second.Page)).ToList()); // what the old all-pages check would have accepted
    }

    [Fact]
    public void The_css_of_a_page_is_computed_once()
    {
        var assets = Assets();
        var page = CaseOf("products").Page;

        Assert.Same(assets.CssOf(page), assets.CssOf(page));
    }

    // ---- error answers: antiforgery 400 is an error page, plain-text 400 is not a page ----

    [Fact]
    public void A_400_page_typed_as_anything_but_an_error_page_is_reported()
    {
        foreach (var wrong in new[] { UiPageType.NotFound, UiPageType.Login, UiPageType.Forbidden })
        {
            AssertReports(Problems("rejected-request", CaseOf("invalid-request", type: wrong)), "must be an error-type page");
        }
    }

    [Fact]
    public void The_not_found_page_answering_400_is_what_the_rule_exists_to_reject()
    {
        var notFound = CaseOf("not-found");
        var asBadRequest = new UiPageCase
        {
            Label = "not found with 400",
            Page = Respond("/login", HttpStatusCode.BadRequest, Fixtures["not-found"].Html, "text/html; charset=utf-8"),
            Type = UiPageType.NotFound,
            Authenticated = notFound.Authenticated,
        };

        var problems = Problems("rejected-request", asBadRequest);

        AssertReports(problems, "must be an error-type page");
        AssertReports(problems, "must not say that something was not found");
        AssertReports(problems, "exactly one data-page=\"error\" region, found 0");
        AssertReports(problems, "must tell, in Spanish, that the request is invalid or has expired");
    }

    [Fact]
    public void Plain_text_400_answers_and_other_statuses_are_not_inspected()
    {
        var plain = new UiPageCase { Label = "logout 400", Page = Respond("/logout", HttpStatusCode.BadRequest, "Bad Request", "text/plain; charset=utf-8"), Type = UiPageType.Error, Authenticated = true };
        var notFound = CaseOf("not-found");

        AssertNoProblems(Problems("rejected-request", plain));
        AssertNoProblems(Problems("rejected-request", notFound));
    }

    // ---- input hints are demanded only where a visible, usable form is offered ----

    private const string EditFields = """
        <input type="hidden" name="__RequestVerificationToken" value="x"><input type="hidden" name="Edit.RowVersion" value="AAAA">
        <label for="name">Nombre</label><input id="name" name="Edit.Name" autocomplete="off">
        <label for="price">Precio</label><input id="price" name="Edit.Price" inputmode="decimal" autocomplete="off">
        <label for="stock">Stock</label><input id="stock" name="Edit.Stock" inputmode="numeric" autocomplete="off">
        """;

    private const string StaleNotice = """<div role="alert">Otra persona modificó este producto. <a href="/products/11111111-1111-1111-1111-111111111111/edit">Recargar el producto</a></div>""";

    private static string EditMain(string content) => $"""<div data-page="product-edit"><h1>Editar producto</h1>{content}<a href="/products">Volver a productos</a></div>""";

    private static UiPageCase EditPage(string content) => CaseOf("products", Shell(EditMain(content), "/products", "/products"), UiPageType.ProductEdit);

    private static string EditForm(string fields = EditFields, string button = """<button type="submit">Guardar</button>""")
        => $"""<form method="post" action="/products/11111111-1111-1111-1111-111111111111/edit">{fields}{button}</form>""";

    [Fact]
    public void A_product_edit_page_with_its_form_passes_the_input_hints()
    {
        AssertNoProblems(Problems("input-hints", EditPage(EditForm())));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void A_stale_conflict_product_edit_page_without_a_usable_form_needs_no_inputs(bool formHidden, bool formHasButton)
    {
        var form = EditForm(button: formHasButton ? """<button type="submit">Guardar</button>""" : string.Empty);
        var content = formHidden ? $"<div hidden>{form}</div>" : form;

        AssertNoProblems(Problems("input-hints", EditPage(StaleNotice + content)));
    }

    [Fact]
    public void A_stale_conflict_product_edit_page_with_no_form_at_all_needs_no_inputs()
    {
        AssertNoProblems(Problems("input-hints", EditPage(StaleNotice)));
    }

    [Fact]
    public void A_hidden_edit_form_is_not_required_to_carry_hints_but_a_usable_one_is()
    {
        var withoutHints = EditFields.Replace(""" inputmode="decimal" """, " ").Replace(""" inputmode="numeric" """, " ");
        Assert.NotEqual(EditFields, withoutHints);

        AssertNoProblems(Problems("input-hints", EditPage(StaleNotice + $"<div hidden>{EditForm(withoutHints)}</div>")));
        var withoutNumbers = EditFields.Replace("Edit.Price", "Edit.Importe").Replace("Edit.Stock", "Edit.Existencias");
        AssertNoProblems(Problems("input-hints", EditPage(StaleNotice + EditForm(withoutNumbers, button: """<button type="submit" disabled>Guardar</button>"""))));
        AssertReports(Problems("input-hints", EditPage(EditForm(withoutNumbers))), "found no 'price' field in a POST form on this ProductEdit page");
        var problems = Problems("input-hints", EditPage(EditForm(withoutHints)));
        AssertReports(problems, "must have inputmode=\"decimal\"");
        AssertReports(problems, "must have inputmode=\"numeric\"");
    }

    [Theory]
    [InlineData("""inputmode="decimal" """, "must have inputmode=\"decimal\"")]
    [InlineData("""inputmode="numeric" """, "must have inputmode=\"numeric\"")]
    [InlineData("""name="Edit.Price" """, "found no 'price' field in a POST form on this ProductEdit page")]
    [InlineData("""name="Edit.Stock" """, "found no 'stock' field in a POST form on this ProductEdit page")]
    public void A_product_edit_page_with_a_usable_form_still_fails_when_a_hint_is_missing(string target, string expected)
    {
        var fields = EditFields.Replace(target, string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(EditFields, fields);

        AssertReports(Problems("input-hints", EditPage(EditForm(fields))), expected);
    }

    [Fact]
    public void A_view_only_products_page_with_a_hidden_create_form_needs_no_inputs()
    {
        var withoutHints = CreateForm.Replace(""" inputmode="decimal" """, " ").Replace(""" inputmode="numeric" """, " ");
        var viewOnly = Broken("products", (CreateForm, $"<div hidden>{withoutHints}</div>"));

        AssertNoProblems(Problems("input-hints", CaseOf("products", viewOnly)));
        AssertNoProblems(Problems("input-hints", CaseOf("products", Broken("products", (CreateForm, string.Empty)))));
        var unusableWithoutNumbers = CreateForm.Replace("Create.Price", "Create.Importe").Replace("Create.Stock", "Create.Existencias").Replace("""<button type="submit">Crear</button>""", string.Empty, StringComparison.Ordinal);
        AssertNoProblems(Problems("input-hints", CaseOf("products", Broken("products", (CreateForm, unusableWithoutNumbers)))));
    }

    private const string CreateUserForm = """
        <form method="post" action="/users"><input type="hidden" name="__RequestVerificationToken" value="x">
        <label for="new-email">Correo electrónico</label><input id="new-email" name="Input.Email" type="email" autocomplete="off">
        <label for="new-password">Contraseña</label><input id="new-password" name="Input.Password" type="password" autocomplete="new-password">
        <button type="submit">Crear</button></form>
        """;

    private const string PermissionFormHtml = """<form method="post" action="/users/permissions/grant" data-action="grant"><input type="hidden" name="userId" value="1"><input type="hidden" name="permission" value="ProductsView"><button type="submit" aria-label="Conceder: Ver productos">Conceder</button></form>""";

    private const string ConfirmationFormHtml = """<form method="post" action="/users/1/remove"><input type="hidden" name="userId" value="1"><button type="submit">Quitar</button></form>""";

    private static UiPageCase UsersPage(string content)
        => CaseOf("products", Shell($"""<div data-page="users"><h1>Usuarios</h1>{content}</div>""", "/users", "/products"), UiPageType.Users);

    [Fact]
    public void A_users_page_with_the_create_form_passes_and_fails_without_its_hints()
    {
        AssertNoProblems(Problems("input-hints", UsersPage(CreateUserForm + PermissionFormHtml)));
        AssertReports(Problems("input-hints", UsersPage(CreateUserForm.Replace("""type="password" """, """type="text" """, StringComparison.Ordinal))), "found no password field on this Users page");
        AssertReports(Problems("input-hints", UsersPage(CreateUserForm.Replace("""autocomplete="new-password">""", """autocomplete="off">""", StringComparison.Ordinal))), "autocomplete=\"new-password\"");
    }

    [Fact]
    public void A_view_only_users_page_needs_no_inputs_whatever_other_forms_it_has()
    {
        AssertNoProblems(Problems("input-hints", UsersPage($"<div hidden>{CreateUserForm.Replace("""autocomplete="new-password">""", """autocomplete="off">""", StringComparison.Ordinal)}</div>")));
        AssertNoProblems(Problems("input-hints", UsersPage(CreateUserForm.Replace("""<button type="submit">Crear</button>""", string.Empty, StringComparison.Ordinal))));
        AssertNoProblems(Problems("input-hints", UsersPage(PermissionFormHtml)));
        AssertNoProblems(Problems("input-hints", UsersPage(ConfirmationFormHtml + PermissionFormHtml)));
    }

    [Fact]
    public void Hidden_credentials_of_the_demo_shortcut_forms_are_not_fields_to_hint()
    {
        const string Shortcuts = """
            <section data-testid="demo-login-panel"><h2>Cuentas de demostración</h2>
            <form method="post" action="/login"><input type="hidden" name="Credentials.Email" value="dueno@demo.test"><input type="hidden" name="Credentials.Password" value="Demo-Pass-123"><button type="submit">Entrar como propietario</button></form>
            <form method="post" action="/login"><input type="hidden" name="Credentials.Email" value="lector@demo.test"><input type="hidden" name="Credentials.Password" value="Demo-Pass-123"><button type="submit">Entrar como lector</button></form>
            </section>
            """;
        var login = Broken("login-rejected", ("</div></main>", "</div>" + Shortcuts + "</main>"));

        AssertNoProblems(Problems("input-hints", CaseOf("login-rejected", login)));
        var demo = CaseOf("home-owner", Broken("home-owner", ("</main>", Shortcuts + "</main>")), UiPageType.Demo);
        AssertNoProblems(Problems("input-hints", demo));
    }

    // ---- a page can only satisfy its own type ----

    [Fact]
    public void A_not_found_or_error_page_cannot_pass_as_another_page_type()
    {
        foreach (var impostor in new[] { UiPageType.NotFound, UiPageType.Error, UiPageType.Forbidden })
        {
            var html = Broken("products", ("data-page=\"products\"", $"data-page=\"{UiPageTypes.DataPage(impostor)}\""));

            AssertReports(Problems("page-region", CaseOf("products", html)), "but this is a \"products\" page");
        }
    }

    [Fact]
    public void The_data_page_names_of_the_pager_do_not_count_as_page_regions()
    {
        var withPager = Broken("products", ("</main>", """<nav aria-label="Paginación" data-testid="pager" data-page="1" data-page-count="2"></nav></main>"""));

        AssertNoProblems(Problems("page-region", CaseOf("products", withPager)));
    }
}
