extern alias ApiHost;

using System.Net;
using System.Text.Json;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>
/// The "Cuentas de demostración" shortcuts on /login and the drift guard: every account the Web displays (on /login and on /demo)
/// must really sign in through the Web against the API with the demo data seeded, with the password the page displays.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class DemoAccountsLoginTests(TestApiFactory api)
{
    private const string WrongDemoPassword = "Custom-Pass-2027-Zq";

    // ---- the panel ----

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("False")]
    public async Task With_show_accounts_off_the_login_page_has_no_demo_panel_and_exactly_one_password_form(string? setting)
    {
        using var host = WebTestHost.Create(api, settings: new WebSettings("Production", setting));
        using var session = host.NewSession();

        var page = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);

        var loginForm = page.FindPasswordForm(); // precondition: it is the real login page
        Assert.NotNull(FormModel.From(page, loginForm).Get("email"));
        Assert.Empty(DemoFlows.ShortcutForms(page));
        DemoFlows.AssertNoDemoAccountMaterial(page);
        Assert.DoesNotContain(SpanishText.DemoAccountsHeading, page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SpanishText.SignInAs, page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(page.Document.Forms, f => f.QuerySelector("input[type=password]") is not null);
        Assert.Equal(0, host.Api.CallCount);
    }

    [Fact]
    public async Task With_show_accounts_on_the_panel_has_one_antiforgery_protected_post_form_per_account_and_the_login_form_is_unchanged()
    {
        using var host = WebTestHost.CreateDemo(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);

        var shortcuts = DemoFlows.Shortcuts(page);
        Assert.Equal(DemoAccounts.AllEmails.Order(), shortcuts.Select(s => s.Email).Order());
        Assert.Contains(SpanishText.DemoAccountsHeading, page.Text, StringComparison.OrdinalIgnoreCase);
        foreach (var (email, password, form, buttonText) in shortcuts)
        {
            Assert.Equal(DemoAccounts.Password, password);
            Assert.False(string.IsNullOrEmpty(form.Get(FormModel.AntiforgeryField)), $"The shortcut of {email} must carry an antiforgery token.");
            Assert.StartsWith(SpanishText.SignInAs, buttonText, StringComparison.OrdinalIgnoreCase);
            Assert.True(buttonText.Length > SpanishText.SignInAs.Length + 1, $"The shortcut of {email} must say which account it signs in as, got '{buttonText}'.");
        }

        Assert.All(DemoFlows.ShortcutForms(page), f =>
        {
            Assert.True(ProductFlows.IsPost(f));
            Assert.Null(f.QuerySelector("input[type=password]"));
            Assert.False(PageContract.IsHidden(f));
            Assert.True(PageContract.IsUsable(f));
        });
        // The existing contract holds: exactly one form with a password input, and it is the real login form, outside the panel.
        var loginForm = page.FindPasswordForm();
        Assert.Null(loginForm.Closest(PageContract.DemoLoginPanel));
        Assert.Equal(0, host.Api.CallCount);
        var register = (await session.GetAsync("/register")).ShouldBe(HttpStatusCode.OK);
        Assert.NotNull(register.FindPasswordForm()); // precondition: the real registration page
        Assert.Empty(register.Select(PageContract.DemoLoginPanel)); // the panel belongs on /login only
    }

    [Fact]
    public async Task The_real_login_form_still_works_while_the_panel_is_shown()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.CreateDemo(api);
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, owner.Email, ApiSeed.StrongPassword);

        response.ShouldRedirectTo("/");
        Assert.True(session.HasAuthCookie);
        var wrong = await WebFlows.LoginAsync(host.NewSession(), owner.Email, "Wr0ng-Pass-Value-123");
        Assert.Contains(SpanishText.InvalidCredentials, wrong.Text);
        Assert.NotEmpty(DemoFlows.ShortcutForms(wrong)); // the panel is still offered after a failed attempt
    }

    // ---- signing in through a shortcut ----

    [Theory]
    [MemberData(nameof(AccountKeys))]
    public async Task A_shortcut_signs_in_through_the_normal_login_flow_as_exactly_that_account(string key)
    {
        var account = DemoAccounts.Get(key);
        using var host = WebTestHost.CreateDemo(api);
        using var session = host.NewSession();
        var loginPage = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(loginPage, DemoFlows.ShortcutFor(loginPage, account.Email));
        var callsBefore = host.Api.CallCount;

        var response = await session.SubmitAsync(form);

        response.ShouldRedirectTo("/");
        WebFlows.AssertNotCacheable(response);
        DemoFlows.AssertHardenedAuthCookies(response);
        Assert.True(session.HasAuthCookie);
        var login = Assert.Single(host.Api.Calls.Skip(callsBefore), c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath == "/api/auth/login");
        Assert.Equal(HttpStatusCode.OK, login.Status);
        using (var sent = JsonDocument.Parse(login.Body!))
        {
            Assert.Equal(account.Email, sent.RootElement.GetProperty("email").GetString());
            Assert.Equal(DemoAccounts.Password, sent.RootElement.GetProperty("password").GetString());
        }

        // Signed in as that account: owner flag and permissions from the API, and the tenant's own catalog.
        var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(account.IsOwner ? "true" : "false", home.Document.QuerySelector(PageContract.ProfileIsOwner)?.GetAttribute("data-value"));
        Assert.Equal(account.Permissions.Select(p => p.ToString()).Order(), home.Select(PageContract.ProfilePermission).Select(e => e.GetAttribute("data-permission") ?? string.Empty).Order());
        var canView = account.IsOwner || account.Permissions.Contains(Permission.ProductsView);
        if (canView)
        {
            var own = await ProductFlows.ListAsync(session, ProductFlows.ListUrl(search: account.ListedSku));
            Assert.Equal(account.ListedSku, Assert.Single(ProductFlows.Rows(own)).Sku);
            var foreign = await ProductFlows.ListAsync(session, ProductFlows.ListUrl(search: account.ForeignSku));
            Assert.Empty(ProductFlows.Rows(foreign));
        }
        else
        {
            (await session.GetAsync("/products")).ShouldBe(HttpStatusCode.Forbidden);
        }

        WebFlows.AssertNoJwtLeaked(session, host);
        WebFlows.AssertNoSecretLogged(host, DemoAccounts.Password);
    }

    public static TheoryData<string> AccountKeys => DemoAccounts.Keys;

    [Theory]
    [InlineData("/products", "/products")]
    [InlineData("/users?page=2", "/users?page=2")]
    [InlineData("//evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("/\\evil.example", "/")]
    public async Task A_shortcut_follows_the_same_return_url_rules_as_the_login_form(string returnUrl, string expected)
    {
        using var host = WebTestHost.CreateDemo(api);
        using var session = host.NewSession();
        var loginPage = (await session.GetAsync("/login?returnUrl=" + Uri.EscapeDataString(returnUrl))).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(loginPage, DemoFlows.ShortcutFor(loginPage, DemoAccounts.AcmeOwnerEmail));

        var response = await session.SubmitAsync(form);

        response.ShouldRedirectTo(expected);
        Assert.True(session.HasAuthCookie);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("garbage")]
    [InlineData("other-session")]
    public async Task A_shortcut_with_a_bad_antiforgery_token_is_rejected_before_the_api_is_called(string tampering)
    {
        using var host = WebTestHost.CreateDemo(api);
        using var session = host.NewSession();
        var loginPage = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(loginPage, DemoFlows.ShortcutFor(loginPage, DemoAccounts.AcmeOwnerEmail));
        switch (tampering)
        {
            case "missing":
                form.WithoutAntiforgeryToken();
                break;
            case "garbage":
                form.WithAntiforgeryToken("CfDJ8-this-is-not-a-valid-token");
                break;
            default:
                using (var other = host.NewSession())
                {
                    form.WithAntiforgeryToken((await other.GetAsync("/login")).ShouldBe(HttpStatusCode.OK).AntiforgeryToken()!);
                }

                break;
        }

        var response = await session.SubmitAsync(form);

        response.ShouldBe(HttpStatusCode.BadRequest);
        WebFlows.AssertNoCookieIssued(session, response);
        Assert.Equal(0, host.Api.CallCount);
        WebFlows.AssertNoStackTrace(response);
    }

    [Fact]
    public async Task A_shortcut_with_a_configured_password_the_api_does_not_know_is_a_controlled_invalid_credentials_page()
    {
        using var host = WebTestHost.CreateDemo(api, WrongDemoPassword);
        using var session = host.NewSession();
        var loginPage = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
        var shortcut = FormModel.From(loginPage, DemoFlows.ShortcutFor(loginPage, DemoAccounts.AcmeOwnerEmail));
        Assert.Equal(WrongDemoPassword, shortcut.Get("password")); // precondition: the shortcut carries the configured password

        var response = await session.SubmitAsync(shortcut);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Contains(SpanishText.InvalidCredentials, response.Text);
        WebFlows.AssertNoCookieIssued(session, response);
        WebFlows.AssertNoStackTrace(response);
        Assert.Equal(HttpStatusCode.Unauthorized, host.Api.Calls.Last(c => c.Uri.AbsolutePath == "/api/auth/login").Status);
        Assert.NotEmpty(DemoFlows.ShortcutForms(response));
    }

    [Theory]
    [InlineData("connection refused")]
    [InlineData("500 problem")]
    [InlineData("200 html")]
    public async Task A_shortcut_degrades_to_unavailable_when_the_backend_misbehaves(string failure)
    {
        using var host = WebTestHost.CreateDemo(api);
        using var session = host.NewSession();
        var loginPage = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(loginPage, DemoFlows.ShortcutFor(loginPage, DemoAccounts.AcmeOwnerEmail));
        host.Api.Intercept = FakeApi.Failure(failure);

        var response = await session.SubmitAsync(form);

        Assert.Contains(response.Status, new[] { 200, 503 });
        Assert.Contains(SpanishText.Unavailable, response.Text, StringComparison.OrdinalIgnoreCase);
        WebFlows.AssertNoCookieIssued(session, response);
        WebFlows.AssertNoStackTrace(response);
        Assert.NotEmpty(DemoFlows.ShortcutForms(response));
    }

    [Fact]
    public async Task A_shortcut_reports_throttling_with_the_retry_after_seconds()
    {
        using var host = WebTestHost.CreateDemo(api);
        using var session = host.NewSession();
        var loginPage = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(loginPage, DemoFlows.ShortcutFor(loginPage, DemoAccounts.AcmeOwnerEmail));
        host.Api.Intercept = _ => FakeApi.TooManyRequests(37);

        var response = await session.SubmitAsync(form);

        Assert.Contains(SpanishText.TooManyAttempts, response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("37", response.Text);
        WebFlows.AssertNoCookieIssued(session, response);
    }

    // ---- drift guard: whatever the Web displays must really work ----

    [Fact]
    public async Task Every_account_the_login_page_displays_signs_in_with_its_shortcut()
    {
        using var host = WebTestHost.CreateDemo(api);
        using var probe = host.NewSession();
        var displayed = DemoFlows.Shortcuts((await probe.GetAsync("/login")).ShouldBe(HttpStatusCode.OK));
        Assert.Equal(DemoAccounts.AllEmails.Order(), displayed.Select(d => d.Email).Order()); // precondition: it displays exactly the seeded accounts

        foreach (var (email, _, _, _) in displayed)
        {
            using var session = host.NewSession();
            var loginPage = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
            var response = await session.SubmitAsync(FormModel.From(loginPage, DemoFlows.ShortcutFor(loginPage, email)));

            Assert.True(response.IsRedirect && session.HasAuthCookie, $"The displayed account {email} did not sign in through its shortcut (status {response.Status}). Page text: {response.Text}");
        }
    }

    [Fact]
    public async Task Every_account_the_demo_page_displays_signs_in_through_the_normal_login_form_with_the_displayed_password()
    {
        using var host = WebTestHost.CreateDemo(api);
        using var probe = host.NewSession();
        var tour = (await probe.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);
        var emails = DemoFlows.TableEmails(tour);
        var password = Assert.Single(DemoFlows.DisplayedPasswords(tour).Distinct());
        Assert.Equal(DemoAccounts.AllEmails.Order(), emails.Order()); // precondition: it displays exactly the seeded accounts

        foreach (var email in emails)
        {
            using var session = host.NewSession();
            var response = await WebFlows.LoginAsync(session, email, password);

            Assert.True(response.IsRedirect && session.HasAuthCookie, $"The displayed account {email} / {password} did not sign in (status {response.Status}). Page text: {response.Text}");
        }
    }

    [Fact]
    public async Task The_login_page_and_the_demo_page_display_the_same_accounts()
    {
        using var host = WebTestHost.CreateDemo(api);
        using var session = host.NewSession();

        var login = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
        var tour = (await session.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(DemoFlows.TableEmails(tour).Order(), DemoFlows.Shortcuts(login).Select(s => s.Email).Order());
        Assert.NotEmpty(DemoFlows.TableEmails(tour));
        Assert.Equal(DemoFlows.DisplayedPasswords(tour).Distinct().Single(), DemoFlows.Shortcuts(login).Select(s => s.Password).Distinct().Single());
    }

    [Fact]
    public async Task Several_visitors_using_different_shortcuts_at_once_never_swap_identities()
    {
        using var host = WebTestHost.CreateDemo(api);

        var results = await Task.WhenAll(DemoAccounts.All.Select(async account =>
        {
            using var session = host.NewSession();
            var loginPage = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
            var response = await session.SubmitAsync(FormModel.From(loginPage, DemoFlows.ShortcutFor(loginPage, account.Email)));
            await Task.Yield();
            var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
            var tenantId = home.Document.QuerySelector(PageContract.ProfileTenantId)!.TextContent.Trim();
            var isOwner = home.Document.QuerySelector(PageContract.ProfileIsOwner)!.GetAttribute("data-value");
            return (account, response.IsRedirect, tenantId, isOwner);
        }));

        Assert.All(results, r => Assert.True(r.Item2));
        Assert.All(results, r => Assert.Equal(r.account.IsOwner ? "true" : "false", r.isOwner));
        var acmeTenants = results.Where(r => r.account.Email.EndsWith("@acme-demo.example")).Select(r => r.tenantId).Distinct().ToList();
        var globexTenants = results.Where(r => r.account.Email.EndsWith("@globex-demo.example")).Select(r => r.tenantId).Distinct().ToList();
        Assert.Single(acmeTenants);
        Assert.Single(globexTenants);
        Assert.NotEqual(acmeTenants[0], globexTenants[0]);
    }
}
