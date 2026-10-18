using System.Net;
using Conversia.TestSupport;
using Conversia.Web.IntegrationTests.Infrastructure;
using Xunit.Sdk;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>
/// The English-leak scanner is itself a test instrument, so it is tested: it must flag English wherever a person can read it
/// (text and the user-visible attributes), stay quiet about Spanish, identifiers, data and the technology names the interface
/// may legitimately keep, and refuse to call an empty or non-rendered page "clean". These tests need no server.
/// </summary>
public class EnglishLeakScannerTests
{
    private const string SpanishLoginPage = """
        <!DOCTYPE html><html lang="es"><head><title>Iniciar sesión - Conversia</title></head>
        <body><nav aria-label="Navegación principal"><a href="/">Inicio</a> <a href="/demo">Recorrido</a></nav>
        <h1>Iniciar sesión</h1>
        <form method="post"><label for="e">Correo electrónico</label><input id="e" name="email" placeholder="tu@correo.com" title="Escribe tu correo">
        <label for="p">Contraseña</label><input id="p" type="password" name="password">
        <button type="submit">Entrar</button><input type="submit" value="Entrar como propietario"></form>
        <p>Las cuentas de demostración usan el mismo token JWT contra la API; el stock y el SKU no cambian. Docker y Blazor se mencionan sin traducir.</p>
        </body></html>
        """;

    private static PageResponse Page(string html, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = new HttpResponseMessage(status);
        return new PageResponse(new Uri("https://localhost/prueba"), response, html);
    }

    private static string Wrap(string body, string lang = "es", string title = "Productos - Conversia")
        => $"<!DOCTYPE html><html lang=\"{lang}\"><head><title>{title}</title></head><body>{body}</body></html>";

    private const string SpanishFiller = "<p>Esta es una página de prueba con texto suficiente para el análisis de la interfaz.</p>";

    // ---- it finds English wherever a person can read it ----

    [Theory]
    [InlineData("<h1>Sign in</h1>", "text in <h1>", "sign in")]
    [InlineData("<a href='/x'>Register</a>", "text in <a>", "register")]
    [InlineData("<button>Delete</button>", "text in <button>", "delete")]
    [InlineData("<label>Password</label>", "text in <label>", "password")]
    [InlineData("<p>Page 1 of 3</p>", "text in <p>", "page")]
    [InlineData("<p>Something went wrong. Please try later.</p>", "text in <p>", "something went wrong")]
    [InlineData("<p>That email is already registered.</p>", "text in <p>", "already")]
    [InlineData("<p>The service is currently unavailable.</p>", "text in <p>", "unavailable")]
    [InlineData("<input placeholder='Search by SKU or name'>", "input[placeholder]", "search")]
    [InlineData("<a href='/x' title='Go back to products'>enlace</a>", "a[title]", "go back")]
    [InlineData("<nav aria-label='Pages'>x</nav>", "nav[aria-label]", "pages")]
    [InlineData("<img src='x.png' alt='Welcome banner'>", "img[alt]", "welcome")]
    [InlineData("<input type='submit' value='Submit'>", "input[value]", "submit")]
    [InlineData("<button type='submit' value='Cancel'>Aceptar</button>", "button[value]", "cancel")]
    [InlineData("<span class='x' aria-label='Not allowed'>No</span>", "span[aria-label]", "not")]
    public void English_is_found_in_the_text_and_in_each_user_visible_attribute(string body, string where, string term)
    {
        var scanned = SpanishUi.Extract(Page(Wrap(body + SpanishFiller)));

        var leaks = SpanishUi.Leaks(scanned);

        Assert.Contains(leaks, l => l.Contains($"\"{term}\"", StringComparison.OrdinalIgnoreCase) && l.Contains(where, StringComparison.Ordinal));
    }

    [Fact]
    public void English_in_the_title_and_the_meta_description_is_found()
    {
        var html = "<!DOCTYPE html><html lang='es'><head><title>Edit product</title><meta name='description' content='Manage your users'></head><body>" + SpanishFiller + "</body></html>";

        var leaks = SpanishUi.Leaks(SpanishUi.Extract(Page(html)));

        Assert.Contains(leaks, l => l.Contains("<title>") && l.Contains("\"edit\""));
        Assert.Contains(leaks, l => l.Contains("meta description") && l.Contains("\"manage\""));
    }

    [Theory]
    [InlineData("SIGN IN")]
    [InlineData("sign in")]
    [InlineData("Sign-in")]
    [InlineData("Log out")]
    public void Matching_ignores_case_and_accepts_a_hyphen_or_a_space_in_phrases(string text)
        => Assert.NotEmpty(EnglishLeaks.Find(text));

    [Theory]
    [InlineData("One or more validation errors occurred.")]
    [InlineData("The Email field is not a valid e-mail address.")]
    [InlineData("A non-empty request body is required.")]
    [InlineData("The JSON value could not be converted to System.Int32. Path: $.price | LineNumber: 0")]
    [InlineData("Unauthorized")]
    [InlineData("Too Many Requests")]
    public void The_untranslated_framework_messages_are_leaks(string text)
    {
        Assert.NotEmpty(EnglishLeaks.Find(text));
        Assert.True(EnglishLeaks.IsFrameworkDefault(text), $"'{text}' should be recognised as an untranslated framework message.");
    }

    [Fact]
    public void Framework_defaults_match_as_whole_words_so_spanish_cognates_are_not_flagged()
    {
        Assert.NotEmpty(EnglishLeaks.Find("Conflict"));
        Assert.NotEmpty(EnglishLeaks.Find("Unsupported Media Type"));
        Assert.Empty(EnglishLeaks.Find("Conflicto de versiones"));
        Assert.Empty(EnglishLeaks.Find("Tipo de contenido no admitido"));
        Assert.False(EnglishLeaks.IsFrameworkDefault("Conflicto"));
    }

    // ---- it stays quiet about Spanish, identifiers and legitimate names ----

    [Fact]
    public void A_spanish_page_with_the_allowed_loanwords_and_technology_names_is_clean()
    {
        var page = Page(SpanishLoginPage);

        var scanned = SpanishUi.Extract(page);

        Assert.Empty(SpanishUi.Leaks(scanned));
        var report = new SpanishUiReport();
        report.Check("spanish login", page);
        report.AssertClean();
    }

    [Theory]
    [InlineData("stock")]
    [InlineData("Stock disponible")]
    [InlineData("Correo email del tenant")]
    [InlineData("SKU-001")]
    [InlineData("Cuentas demo con login y logout")]
    [InlineData("JWT, API, Docker, Blazor y token")]
    [InlineData("Recorrido tour de la plantilla")]
    [InlineData("Ver usuarios")]
    [InlineData("Gestionar productos")]
    [InlineData("Ocurrió un error inesperado. Inténtalo de nuevo más tarde.")]
    [InlineData("Correo o contraseña incorrectos.")]
    [InlineData("Demasiados intentos. Vuelve a intentarlo en 60 segundos.")]
    [InlineData("Ya existe un producto con ese SKU.")]
    [InlineData("Otra persona modificó este producto. Recarga la página.")]
    public void Spanish_text_and_the_loanwords_of_the_allowlist_are_not_flagged(string text)
        => Assert.Empty(EnglishLeaks.Find(text));

    [Theory]
    [InlineData("UsersManage")]
    [InlineData("tenantName")]
    [InlineData("ProductsView")]
    [InlineData("Pages2")]
    [InlineData("userId")]
    [InlineData("backup")]
    [InlineData("rename")]
    public void Terms_only_match_as_whole_words_never_inside_an_identifier_or_another_word(string text)
        => Assert.Empty(EnglishLeaks.Find(text));

    [Theory]
    [InlineData("owner@acme-demo.example")]
    [InlineData("Escribe a viewer@acme-demo.example para ayuda")]
    [InlineData("https://localhost/products?page=2")]
    [InlineData("Conversia")]
    [InlineData("Conversia.Web")]
    [InlineData("Una API de ASP.NET Core sobre PostgreSQL")]
    [InlineData("Se levanta con Docker Compose")]
    [InlineData("Contraseña de demostración Demo-Pass-2026!")]
    public void The_allowlist_removes_addresses_urls_and_names_before_matching(string text)
        => Assert.Empty(EnglishLeaks.Find(text));

    [Fact]
    public void Data_cells_scripts_and_data_attributes_are_not_scanned()
    {
        const string body = """
            <table><tr data-testid="product-row"><td data-field="sku">SKU-1</td><td data-field="name">Widget Pro Edition</td>
            <td data-field="price" data-price="5.00">5,00</td></tr></table>
            <div data-permission="UsersManage" data-user-id="x">Gestionar usuarios</div>
            <script>var message = "Sign in";</script><style>.password::after { content: "Delete"; }</style>
            """;

        var scanned = SpanishUi.Extract(Page(Wrap(body + SpanishFiller)));

        Assert.Empty(SpanishUi.Leaks(scanned));
        Assert.DoesNotContain(scanned, s => s.Text.Contains("Widget"));
    }

    // ---- code, pre, kbd and samp: routes, JSON and commands are exempt; English prose in them is not ----

    [Theory]
    [InlineData("<code>Sign in</code>", "sign in")]
    [InlineData("<pre>Something went wrong</pre>", "something went wrong")]
    [InlineData("<samp>Please try again later</samp>", "try again")]
    [InlineData("<code>Delete</code>", "delete")]
    [InlineData("<kbd>Cancel</kbd>", "cancel")]
    [InlineData("<pre><span>Welcome back</span></pre>", "welcome")]
    [InlineData("<pre>$ dotnet run\nSomething went wrong\n</pre>", "something went wrong")]
    [InlineData("<pre>GET /api/users - list of users</pre>", "list")]
    [InlineData("<pre>dotnet test # Run the tests again</pre>", "again")]
    [InlineData("<pre>{ \"ok\": true }\nThe password is wrong</pre>", "password")]
    public void English_prose_inside_code_pre_kbd_and_samp_is_found(string html, string term)
    {
        var scanned = SpanishUi.Extract(Page(Wrap(html + SpanishFiller)));

        var leaks = SpanishUi.Leaks(scanned);

        Assert.Contains(leaks, l => l.Contains($"\"{term}\"", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("<code>GET /api/users</code>")]
    [InlineData("<code>DELETE /api/products/{id}</code>")]
    [InlineData("<code>POST /api/auth/login</code>")]
    [InlineData("<samp>/login</samp>")]
    [InlineData("<code>/api/users?page=2&amp;pageSize=10</code>")]
    [InlineData("<pre>{ \"name\": \"Widget\", \"price\": 5 }</pre>")]
    [InlineData("<pre>{\n  \"email\": \"owner@acme-demo.example\",\n  \"password\": \"Demo-Pass-2026!\"\n}</pre>")]
    [InlineData("<pre>[\n  { \"id\": 1 }\n]</pre>")]
    [InlineData("<pre>$ dotnet ef migrations add Initial</pre>")]
    [InlineData("<code>docker compose up -d</code>")]
    [InlineData("<pre>curl -X DELETE https://localhost:5001/api/products/1</pre>")]
    [InlineData("<pre>git add .\ngit commit -m 'x'</pre>")]
    [InlineData("<pre><span>GET</span> <span>/api/users</span></pre>")]
    [InlineData("<kbd>Enter</kbd>")]
    [InlineData("<kbd>Ctrl+S</kbd>")]
    [InlineData("<kbd>Ctrl</kbd> + <kbd>Delete</kbd>")]
    [InlineData("<code>Demo-Pass-2026!</code>")]
    [InlineData("<code>0HNABC123XYZ:00000001</code>")]
    [InlineData("<code>UsersManage</code>")]
    public void Routes_json_commands_key_names_and_identifiers_inside_code_blocks_are_not_flagged(string html)
    {
        var scanned = SpanishUi.Extract(Page(Wrap(html + SpanishFiller)));

        Assert.Empty(SpanishUi.Leaks(scanned));
        Assert.Empty(SpanishUi.NotSpanish(scanned));
    }

    // ---- identifiers the interface must show (GUIDs, trace ids, SKUs) never count as English prose ----

    [Theory]
    [InlineData("<dl><dt>Inquilino</dt><dd data-testid=\"profile-tenant-id\">3f2a9c1e-7b4d-4e8a-9c3d-1a2b3c4d5e6f</dd></dl>")]
    [InlineData("<dl><dt>Inquilino</dt><dd>3f2a9c1e-7b4d-4e8a-9c3d-1a2b3c4d5e6f 3f2a9c1e-7b4d-4e8a-9c3d-1a2b3c4d5e6f</dd></dl>")]
    [InlineData("<p>Id de solicitud: <code>00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00</code></p>")]
    [InlineData("<code>00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00</code>")]
    [InlineData("<dl><dt>Código</dt><dd>FER0-4bf92f3577b34da6a3ce929d0e0e4736</dd></dl>")]
    [InlineData("<div>FER0-4bf92f3577b34da6a3ce929d0e0e4736</div>")]
    [InlineData("<dd>4bf92f3577b34da6a3ce929d0e0e4736 00f067aa0ba902b7 2026-10-16 21:39:05 1.2.3 42</dd>")]
    [InlineData("<code>Id de solicitud: 00-abc-def-00</code>")]
    [InlineData("<p>Id de solicitud: 00-abc-def-00</p>")]
    public void Guids_trace_ids_skus_dates_and_other_identifiers_are_not_english_prose(string html)
    {
        var scanned = SpanishUi.Extract(Page(Wrap(html + SpanishFiller)));

        Assert.Empty(SpanishUi.Leaks(scanned));
        Assert.Empty(SpanishUi.NotSpanish(scanned));
    }

    [Theory]
    [InlineData("<dd>the quick brown fox</dd>")]
    [InlineData("<code>the quick brown fox</code>")]
    [InlineData("<div>Quick brown fox jumps 3f2a9c1e-7b4d</div>")]
    [InlineData("<code>GET /api/users the quick brown fox</code>")]
    public void English_prose_without_a_spanish_word_is_still_flagged_next_to_identifiers_and_in_code(string html)
    {
        var scanned = SpanishUi.Extract(Page(Wrap(html + SpanishFiller)));

        Assert.NotEmpty(SpanishUi.NotSpanish(scanned));
    }

    [Fact]
    public void Word_count_ignores_identifiers_and_single_tokens()
    {
        Assert.Equal(0, EnglishLeaks.WordCount("3f2a9c1e-7b4d-4e8a-9c3d-1a2b3c4d5e6f"));
        Assert.Equal(0, EnglishLeaks.WordCount("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00"));
        Assert.Equal(1, EnglishLeaks.WordCount("a.b.c.d"));
        Assert.Equal(3, EnglishLeaks.WordCount("Id de solicitud: 00-abc-def-00"));
        Assert.Equal(4, EnglishLeaks.WordCount("the quick brown fox 2026-10-16"));
    }

    [Fact]
    public void Spanish_prose_next_to_a_route_in_a_code_block_is_clean_and_the_route_is_not_scanned()
    {
        var scanned = SpanishUi.Extract(Page(Wrap("<pre>GET /api/users - lista de usuarios de la cuenta</pre>" + SpanishFiller)));

        Assert.Empty(SpanishUi.Leaks(scanned));
        Assert.Contains(scanned, s => s.Text == "- lista de usuarios de la cuenta");
    }

    // ---- the denylist covers the generic UI words the first list missed ----

    [Theory]
    [InlineData("Close")]
    [InlineData("Remove")]
    [InlineData("Open")]
    [InlineData("Enter")]
    [InlineData("Select")]
    [InlineData("Sort")]
    [InlineData("Showing 1-10 of 40")]
    [InlineData("Settings")]
    [InlineData("Profile")]
    [InlineData("Dashboard")]
    [InlineData("Role")]
    [InlineData("Retry")]
    [InlineData("Continue")]
    [InlineData("Confirm")]
    [InlineData("Download")]
    [InlineData("Upload")]
    [InlineData("Choose a file")]
    [InlineData("Remember me")]
    [InlineData("Session expired")]
    [InlineData("Email address")]
    [InlineData("E-mail address")]
    [InlineData("please CLOSE the dialog")]
    public void Generic_english_ui_words_and_phrases_are_found(string text)
        => Assert.NotEmpty(EnglishLeaks.Find(text));

    [Theory]
    [InlineData("Roles")]
    [InlineData("Los roles del equipo")]
    [InlineData("Cerrar")]
    [InlineData("Abrir")]
    [InlineData("Seleccionar archivo")]
    [InlineData("Continúe con el registro")]
    [InlineData("Perfil")]
    [InlineData("Configuración")]
    [InlineData("Panel de control")]
    [InlineData("Subir archivo")]
    [InlineData("Confirmar contraseña")]
    [InlineData("Ordenar por nombre")]
    [InlineData("Documentación en OpenAPI")]
    [InlineData("Correo electrónico")]
    [InlineData("Recordarme")]
    public void The_spanish_equivalents_and_the_spanish_homographs_are_not_flagged(string text)
        => Assert.Empty(EnglishLeaks.Find(text));

    // ---- a long text must be Spanish, whatever words it uses ----

    [Theory]
    [InlineData("<p>Quantum flux capacitor engaged somewhere today</p>", "text in <p>")]
    [InlineData("<span>Totally harmless looking english sentence</span>", "text in <span>")]
    [InlineData("<a href='/x' title='A friendly little tooltip text'>enlace</a>", "a[title]")]
    [InlineData("<pre>Quantum flux capacitor engaged somehow</pre>", "text in <pre>")]
    public void A_text_of_four_or_more_words_without_a_spanish_word_fails_the_report_even_with_no_denylisted_word(string html, string where)
    {
        var page = Page(Wrap(html + SpanishFiller));
        Assert.Empty(SpanishUi.Leaks(SpanishUi.Extract(page))); // precondition: the denylist alone does not catch these
        var report = new SpanishUiReport();
        report.Check("long english", page);

        var failure = Assert.Throws<XunitException>(report.AssertClean);

        Assert.Contains("has no Spanish word", failure.Message);
        Assert.Contains(where, failure.Message);
    }

    [Theory]
    [InlineData("<p>Tornillo hexagonal de acero</p>")]
    [InlineData("<p>Quantum flux</p>")]
    [InlineData("<p>Stock total SKU</p>")]
    [InlineData("<p>Ver todos los productos del catálogo</p>")]
    [InlineData("<p>Eliminar Widget Pro Edition</p>")]
    public void Short_texts_and_spanish_texts_do_not_trip_the_long_text_rule(string html)
    {
        var report = new SpanishUiReport();
        report.Check("short", Page(Wrap(html + SpanishFiller)));

        report.AssertClean();
    }

    // ---- it is not vacuous ----

    [Fact]
    public void An_english_page_fails_the_report_and_names_each_leak()
    {
        var english = Wrap("<h1>Sign in</h1><p>Welcome back. Please enter your password.</p><button>Submit</button><a href='/register'>Register</a>", lang: "en", title: "Sign in");
        var report = new SpanishUiReport();
        report.Check("english login", Page(english));

        var failure = Assert.Throws<XunitException>(report.AssertClean);

        Assert.Contains("lang", failure.Message);
        Assert.Contains("\"sign in\"", failure.Message);
        Assert.Contains("\"password\"", failure.Message);
        Assert.Contains("\"submit\"", failure.Message);
        Assert.Contains("no Spanish word", failure.Message);
    }

    [Fact]
    public void A_half_translated_page_fails_the_report()
    {
        var html = Wrap("<h1>Productos</h1><p>No tienes permiso para ver esta página.</p><button>Delete</button>");
        var report = new SpanishUiReport();
        report.Check("half translated", Page(html));

        var failure = Assert.Throws<XunitException>(report.AssertClean);

        Assert.Contains("\"delete\"", failure.Message);
    }

    [Theory]
    [InlineData("", "no body")]
    [InlineData("<html lang='es'><head><title>Vacía</title></head><body></body></html>", "words were scanned")]
    [InlineData("<html lang='es'><head><title>Vacía</title></head><body><script>alert(1)</script><p>Hola</p></body></html>", "words were scanned")]
    public void A_page_that_did_not_render_anything_is_not_clean(string html, string expectedReason)
    {
        var report = new SpanishUiReport();
        report.Check("empty", Page(html));

        var failure = Assert.Throws<XunitException>(report.AssertClean);

        Assert.Contains("[empty]", failure.Message);
        Assert.Contains(expectedReason, failure.Message);
    }

    [Fact]
    public void A_redirect_is_not_a_rendered_page()
    {
        var report = new SpanishUiReport();
        report.Check("redirect", Page(string.Empty, HttpStatusCode.Found));

        Assert.Throws<XunitException>(report.AssertClean);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("")]
    public void A_wrong_or_missing_html_lang_is_reported(string lang)
    {
        var html = lang.Length == 0
            ? "<!DOCTYPE html><html><head><title>Productos</title></head><body>" + SpanishFiller + "</body></html>"
            : Wrap(SpanishFiller, lang);
        var report = new SpanishUiReport();
        report.Check("lang", Page(html));

        var failure = Assert.Throws<XunitException>(report.AssertClean);

        Assert.Contains("<html lang>", failure.Message);
    }

    [Fact]
    public void An_empty_title_is_reported()
    {
        var report = new SpanishUiReport();
        report.Check("title", Page(Wrap(SpanishFiller, title: " ")));

        var failure = Assert.Throws<XunitException>(report.AssertClean);

        Assert.Contains("<title> is empty", failure.Message);
    }

    [Fact]
    public void A_report_that_checked_nothing_fails()
        => Assert.Throws<XunitException>(new SpanishUiReport().AssertClean);

    [Fact]
    public void A_clean_spanish_page_passes_the_report()
    {
        var report = new SpanishUiReport();
        report.Check("ok", Page(Wrap(SpanishFiller)));
        report.AssertClean();
        Assert.Equal(1, report.PagesChecked);
    }
}
