extern alias ApiHost;

using System.Net;
using Conversia.Web.Authentication;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Admin;

/// <summary>
/// The platform administrator's sign-in and session: its own login page, its own cookie, and no way across to the companies' pages (or back).
/// Everything runs against the real API: the administrator is created in the database and signs in through the Web's real form.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class AdminSessionTests(TestApiFactory api)
{
    private const string AdminCookie = ".Conversia.Admin";
    private const string CompanyCookie = ".Conversia.Auth";

    private static readonly Guid SomeCompany = Guid.Parse("a0a0a0a0-0000-4000-8000-000000000001");

    // ---- the login page ----

    [Fact]
    public async Task The_login_page_names_the_administration_and_offers_no_company_navigation()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync("/admin/login")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Iniciar sesión", page.Document.QuerySelector("main h1")!.Plain().Trim());
        Assert.Contains("Administración de Conversia", page.Document.QuerySelector("aside.auth-brand")!.Plain());
        var links = page.Select("a[href]").Select(a => a.GetAttribute("href")).ToList();
        Assert.DoesNotContain("/register", links);
        Assert.DoesNotContain("/login", links);
        Assert.Equal("noindex", page.Document.QuerySelector("meta[name=robots]")!.GetAttribute("content"));
        Assert.NotNull(page.AntiforgeryToken());
        Assert.Equal("current-password", page.Document.QuerySelector("input[type=password]")!.GetAttribute("autocomplete"));
        WebFlows.AssertNotCacheable(page);
    }

    [Fact]
    public async Task Signing_in_lands_on_the_company_list_and_leaves_one_session_cookie_that_holds_no_token()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.AdminLoginAsync(session, admin.Email, admin.Password);

        response.ShouldRedirectTo("/admin");
        var issued = Assert.Single(WebFlows.IssuedCookies(response));
        Assert.StartsWith(AdminCookie + "=", issued);
        Assert.Contains("httponly", issued, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", issued, StringComparison.OrdinalIgnoreCase);
        Assert.Null(WebFlows.Attribute(issued, "expires")); // a browser-session cookie: closing the browser ends it
        Assert.Null(WebFlows.Attribute(issued, "max-age"));
        Assert.Null(session.CookieValue(CompanyCookie)); // it never starts a company session
        (await session.FollowAsync(response)).ShouldBe(HttpStatusCode.OK);
        WebFlows.AssertNoJwtLeaked(session, host);
        WebFlows.AssertNoSecretLogged(host, admin.Password);
        Assert.Contains(host.Api.Calls, call => call.PathAndQuery == "/api/admin/auth/login");
        Assert.DoesNotContain(host.Api.Calls, call => call.PathAndQuery == "/api/auth/login");
    }

    [Theory]
    [InlineData("/admin/auditoria", "/admin/auditoria")]
    [InlineData("/admin/empresas/a0a0a0a0-0000-4000-8000-000000000001", "/admin/empresas/a0a0a0a0-0000-4000-8000-000000000001")]
    [InlineData("/panel", "/admin")]
    [InlineData("//evil.example/admin", "/admin")]
    [InlineData("https://evil.example/admin", "/admin")]
    public async Task Signing_in_goes_back_only_to_a_page_of_the_administration(string returnUrl, string expected)
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.AdminLoginAsync(session, admin.Email, admin.Password, returnUrl);

        response.ShouldRedirectTo(expected);
    }

    [Fact]
    public async Task A_wrong_password_an_unknown_email_and_a_company_users_credentials_get_the_same_answer()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);

        var wrongPassword = await WebFlows.AdminLoginAsync(host.NewSession(), admin.Email, "Wr0ng-Pass-Value-123");
        var unknown = await WebFlows.AdminLoginAsync(host.NewSession(), ApiSeed.UniqueEmail("nadie"), ApiSeed.AdminPassword);
        var company = await WebFlows.AdminLoginAsync(host.NewSession(), owner.Email, ApiSeed.StrongPassword);

        foreach (var response in new[] { wrongPassword, unknown, company })
        {
            response.ShouldBe(HttpStatusCode.OK);
            Assert.Equal(SpanishText.InvalidCredentials, response.Document.QuerySelector("main .alert--danger")!.Plain().Trim());
            Assert.DoesNotContain(response.Select("input[type=password]"), i => !string.IsNullOrEmpty(i.GetAttribute("value")));
            Assert.Empty(WebFlows.IssuedCookies(response));
        }

        Assert.Equal(wrongPassword.Document.QuerySelector("main .alert")!.OuterHtml, unknown.Document.QuerySelector("main .alert")!.OuterHtml);
        Assert.Equal(wrongPassword.Document.QuerySelector("main .alert")!.OuterHtml, company.Document.QuerySelector("main .alert")!.OuterHtml);
    }

    [Fact]
    public async Task The_administrators_credentials_do_not_open_a_company_session()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, admin.Email, admin.Password);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Contains(SpanishText.InvalidCredentials, response.Text);
        WebFlows.AssertNoCookieIssued(session, response);
    }

    [Fact]
    public async Task A_throttled_sign_in_is_said_in_words_and_an_unavailable_api_is_said_plainly()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        using var host = WebTestHost.Create(api);

        host.Api.Intercept = _ => FakeApi.TooManyRequests(37);
        var throttled = await WebFlows.AdminLoginAsync(host.NewSession(), admin.Email, admin.Password);
        host.Api.Intercept = FakeApi.Failure("connection refused");
        var unavailable = await WebFlows.AdminLoginAsync(host.NewSession(), admin.Email, admin.Password);

        Assert.Contains("Demasiados intentos. Vuelve a intentarlo en 37 segundos.", throttled.Text);
        Assert.Contains("El servicio no está disponible en este momento", unavailable.Text);
        Assert.Empty(WebFlows.IssuedCookies(throttled));
        Assert.Empty(WebFlows.IssuedCookies(unavailable));
    }

    [Fact]
    public async Task A_token_without_a_usable_expiry_is_not_a_session()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        using var host = WebTestHost.Create(api);
        host.Api.Intercept = async request =>
            request.RequestUri!.AbsolutePath == "/api/admin/auth/login"
                ? await FakeApi.Respond(HttpStatusCode.OK, $$"""{"token":"not-a-jwt","adminId":"{{Guid.NewGuid()}}","expiresAt":"2030-01-01T00:00:00Z"}""", "application/json")
                : null;
        using var session = host.NewSession();

        var response = await WebFlows.AdminLoginAsync(session, admin.Email, admin.Password);

        response.ShouldBe(HttpStatusCode.OK);
        WebFlows.AssertNoCookieIssued(session, response);
    }

    // ---- the pages are for the administrator only ----

    public static TheoryData<string> AdminRoutes => new()
    {
        "/admin",
        "/admin/auditoria",
        "/admin/auditoria?empresa=a0a0a0a0-0000-4000-8000-000000000001",
        "/admin/empresas/a0a0a0a0-0000-4000-8000-000000000001",
        "/admin/empresas/a0a0a0a0-0000-4000-8000-000000000001/estado/activar",
        "/admin/empresas/a0a0a0a0-0000-4000-8000-000000000001/estado/suspender",
        "/admin/empresas/a0a0a0a0-0000-4000-8000-000000000001/lineas/a0a0a0a0-0000-4000-8000-000000000002/desactivar",
    };

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task An_anonymous_visitor_is_sent_to_the_administrators_login_and_the_admin_api_is_never_called(string route)
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await session.GetAsync(route);

        AssertSentToAdminLogin(response, route);
        Assert.Equal(0, host.Api.CallCount);
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task A_company_user_is_sent_to_the_administrators_login_too_and_the_admin_api_is_never_called(string route)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var callsBefore = host.Api.CallCount;

        var response = await session.GetAsync(route);

        AssertSentToAdminLogin(response, route);
        Assert.Equal(callsBefore, host.Api.CallCount);
    }

    [Theory]
    [InlineData("/panel")]
    [InlineData("/conversaciones")]
    [InlineData("/organizacion")]
    [InlineData("/users")]
    [InlineData("/cuenta-pendiente")]
    [InlineData("/")]
    public async Task An_administrators_session_opens_no_company_page(string route)
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAdminAsync(host, admin);
        var callsBefore = host.Api.CallCount;

        var response = await session.GetAsync(route);

        if (route == "/")
        {
            response.ShouldBe(HttpStatusCode.OK); // the public home: an administrator is a visitor here
            Assert.Empty(response.Select("[data-testid=profile-tenant-id]"));
        }
        else
        {
            response.ShouldRedirectToLogin();
        }

        Assert.Equal(callsBefore, host.Api.CallCount); // not one call to the company API with the administrator's token
    }

    [Fact]
    public async Task The_two_sessions_live_side_by_side_and_end_independently()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var adminLogin = await WebFlows.AdminLoginAsync(session, admin.Email, admin.Password);
        adminLogin.ShouldRedirectTo("/admin");
        Assert.NotNull(session.CookieValue(AdminCookie));
        Assert.NotNull(session.CookieValue(CompanyCookie));

        (await session.GetAsync("/admin")).ShouldBe(HttpStatusCode.OK);
        (await session.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);
        Assert.Empty((await session.GetAsync("/admin")).Select("a[href='/panel'], a[href='/organizacion']")); // the administrator's shell has no company navigation

        // The company session ends; the administrator's stays.
        var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        (await session.SubmitAsync(FormModel.From(home, home.FindLogoutForm()))).ShouldRedirectTo("/");
        (await session.GetAsync("/organizacion")).ShouldRedirectToLogin();
        (await session.GetAsync("/admin")).ShouldBe(HttpStatusCode.OK);

        // The administrator's session ends; nothing else is touched.
        var adminHome = (await session.GetAsync("/admin")).ShouldBe(HttpStatusCode.OK);
        var logout = await session.SubmitAsync(FormModel.From(adminHome, adminHome.FindLogoutForm()));
        logout.ShouldRedirectTo("/admin/login");
        Assert.True(WebFlows.ClearsAuthCookie(logout));
        AssertSentToAdminLogin(await session.GetAsync("/admin"), "/admin");
    }

    [Fact]
    public async Task A_company_cookie_under_the_administrators_name_opens_nothing_and_the_other_way_round()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var company = await WebFlows.SignedInAsync(host, owner.Email);
        using var administrator = await WebFlows.SignedInAdminAsync(host, admin);
        var companyValue = company.CookieValue(CompanyCookie)!;
        var adminValue = administrator.CookieValue(AdminCookie)!;
        Assert.NotEqual(companyValue, adminValue);

        using var forgedAdmin = host.NewSession();
        forgedAdmin.SetCookie(AdminCookie, companyValue);
        AssertSentToAdminLogin(await forgedAdmin.GetAsync("/admin"), "/admin");

        using var forgedCompany = host.NewSession();
        forgedCompany.SetCookie(CompanyCookie, adminValue);
        (await forgedCompany.GetAsync("/panel")).ShouldRedirectToLogin();
        (await forgedCompany.GetAsync("/organizacion")).ShouldRedirectToLogin();
    }

    [Fact]
    public async Task A_company_session_cannot_post_to_an_admin_form_and_the_admin_api_is_not_called()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var token = await WebFlows.AntiforgeryTokenAsync(session);
        var callsBefore = host.Api.CallCount;

        var quota = await session.SubmitAsync(new FormModel(new Uri(WebTestHost.BaseAddress, $"/admin/empresas/{owner.TenantId}"), "Quota.MaxActiveLines", "99", token));
        var status = await session.SubmitAsync(new FormModel(new Uri(WebTestHost.BaseAddress, $"/admin/empresas/{owner.TenantId}/estado/suspender"), "Input.Reason", "x", token));

        Assert.All(new[] { quota, status }, response => Assert.True(response.IsRedirect || response.Status == 400, $"{response.RequestUri.PathAndQuery}: {response.Status}"));
        Assert.Equal(callsBefore, host.Api.CallCount);
        (await session.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK); // still an active company
    }

    // ---- the lifetime ----

    [Fact]
    public async Task The_session_lasts_as_long_as_the_token_and_does_not_slide()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var clock = new ManualTimeProvider();
        using var host = WebTestHost.Create(api, configureServices: services =>
            services.PostConfigure<CookieAuthenticationOptions>(AuthenticationExtensions.AdminScheme, options => options.TimeProvider = clock));
        using var session = await WebFlows.SignedInAdminAsync(host, admin);

        clock.Advance(TimeSpan.FromMinutes(40));
        var response = (await session.GetAsync("/admin")).ShouldBe(HttpStatusCode.OK);
        Assert.Empty(WebFlows.IssuedCookies(response)); // no renewal: the cookie never outlives the token

        clock.Advance(TimeSpan.FromMinutes(19));
        (await session.GetAsync("/admin")).ShouldBe(HttpStatusCode.OK); // 59 minutes: still inside the token's hour

        clock.Advance(TimeSpan.FromMinutes(3));
        AssertSentToAdminLogin(await session.GetAsync("/admin"), "/admin"); // 62 minutes: over
    }

    [Fact]
    public async Task A_token_the_api_no_longer_accepts_ends_the_session_and_goes_to_the_login()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAdminAsync(host, admin);
        host.Api.Intercept = request => request.RequestUri!.AbsolutePath.StartsWith("/api/admin/", StringComparison.Ordinal)
            ? FakeApi.Respond(HttpStatusCode.Unauthorized)
            : Task.FromResult<HttpResponseMessage?>(null);

        var response = await session.GetAsync("/admin");

        response.ShouldRedirectTo("/admin/login");
        Assert.True(WebFlows.ClearsAuthCookie(response));
        host.Api.Intercept = null;
        AssertSentToAdminLogin(await session.GetAsync("/admin"), "/admin");
    }

    [Fact]
    public async Task Pages_of_the_administration_are_never_stored_and_are_not_indexed()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAdminAsync(host, admin);

        foreach (var route in new[] { "/admin", "/admin/auditoria" })
        {
            var page = (await session.GetAsync(route)).ShouldBe(HttpStatusCode.OK);
            WebFlows.AssertNotStored(page);
            Assert.Equal("noindex", page.Document.QuerySelector("meta[name=robots]")!.GetAttribute("content"));
        }

        WebFlows.AssertNoJwtLeaked(session, host);
    }

    // ---- logout ----

    [Fact]
    public async Task Logging_out_needs_the_antiforgery_token_and_the_administrators_session()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAdminAsync(host, admin);
        var page = (await session.GetAsync("/admin")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(page, page.FindLogoutForm());

        var forged = await session.SubmitAsync(FormModel.From(page, page.FindLogoutForm()).WithoutAntiforgeryToken());
        forged.ShouldBe(HttpStatusCode.BadRequest);
        Assert.False(WebFlows.ClearsAuthCookie(forged));
        (await session.GetAsync("/admin")).ShouldBe(HttpStatusCode.OK);

        var done = await session.SubmitAsync(form);
        done.ShouldRedirectTo("/admin/login");
        Assert.True(WebFlows.ClearsAuthCookie(done));
    }

    // ---- helpers ----

    private static void AssertSentToAdminLogin(PageResponse response, string route)
    {
        Assert.True(response.IsRedirect, $"Expected a redirect to the administrator's login for {route} but got {response.Status}. Page text: {response.Text}");
        var target = new Uri(WebTestHost.BaseAddress, response.RedirectTarget!);
        Assert.Equal("/admin/login", target.AbsolutePath);
        var returnUrl = System.Web.HttpUtility.ParseQueryString(target.Query)["returnUrl"];
        if (returnUrl is not null)
        {
            Assert.Equal(route, returnUrl);
        }
    }
}
