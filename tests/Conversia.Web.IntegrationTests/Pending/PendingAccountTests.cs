extern alias ApiHost;

using System.Net;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pending;

/// <summary>
/// A company that cannot operate yet (Pending after registering, or Suspended) lands on a calm page that says where it stands, finds the
/// operating pages of the shell locked, and is sent back to that page by every operating page: the API's 403 <c>tenant_not_active</c> is an
/// expected state, not an error. Runs against the real API; the test host activates every company except the ones named "PENDING ...".
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class PendingAccountTests(TestApiFactory api)
{
    private const string PendingPage = "/cuenta-pendiente";

    private static readonly string[] OperatingPaths =
    [
        "/panel", "/conversaciones", "/organizacion", "/users", "/",
        "/conversaciones/9b2f1c3e-0000-4000-8000-000000000001", "/organizacion/canales/9b2f1c3e-0000-4000-8000-000000000002/activar",
    ];

    // ---- where a person lands ----

    [Fact]
    public async Task Signing_in_to_a_pending_company_lands_on_the_pending_page_and_says_what_happens_next()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, pending: true);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, owner.Email, ApiSeed.StrongPassword);

        response.ShouldRedirectTo(PendingPage);
        var page = (await session.FollowAsync(response)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Tu empresa está pendiente de activación", page.Document.QuerySelector("main h1")!.Plain().Trim());
        Assert.Equal("Pending", page.Document.QuerySelector("[data-page=account-state]")!.GetAttribute("data-status"));
        var help = page.Document.QuerySelector("[data-testid=pending-help]")!.Plain();
        Assert.Contains("El equipo de Conversia revisa tu registro y activa tu empresa", help);
        Assert.Contains("cuántas líneas de WhatsApp", help);
        Assert.Contains("contacta al equipo de Conversia", help);
        Assert.Empty(page.Select("[data-testid=just-registered]"));
        Assert.Empty(page.Select("[data-testid=error-code]")); // an expected state shows no error code
        WebFlows.AssertNotStored(page);
    }

    [Fact]
    public async Task Registering_a_company_lands_on_the_pending_page_with_the_registered_note()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.RegisterAsync(session, "PENDING " + ApiSeed.UniqueTenantName("Aurora"), ApiSeed.UniqueEmail("nueva"), ApiSeed.StrongPassword);

        response.ShouldRedirectTo(PendingPage + "?registrado=1");
        var page = (await session.FollowAsync(response)).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("Tu empresa quedó registrada y está pendiente de activación.", page.Document.QuerySelector("[data-testid=just-registered]")!.Plain());
    }

    [Fact]
    public async Task Registering_a_company_that_operates_lands_on_the_dashboard()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.RegisterAsync(session, ApiSeed.UniqueTenantName("Activa"), ApiSeed.UniqueEmail("activa"), ApiSeed.StrongPassword);

        response.ShouldRedirectTo("/panel");
    }

    [Fact]
    public async Task The_register_page_says_the_company_needs_activation()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync("/register")).ShouldBe(HttpStatusCode.OK);

        var note = page.Document.QuerySelector("[data-testid=activation-note]")!.Plain();
        Assert.Contains("el equipo de Conversia activa tu empresa", note);
        Assert.Contains("cuántas líneas de WhatsApp", note);
    }

    [Fact]
    public async Task Signing_in_to_a_suspended_company_lands_on_the_suspended_page()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Suspended", "Falta de pago");
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, owner.Email, ApiSeed.StrongPassword);

        response.ShouldRedirectTo(PendingPage);
        var page = (await session.FollowAsync(response)).ShouldBe(HttpStatusCode.OK);
        Assert.Equal("Tu empresa está suspendida", page.Document.QuerySelector("main h1")!.Plain().Trim());
        Assert.Equal("Suspended", page.Document.QuerySelector("[data-page=account-state]")!.GetAttribute("data-status"));
        var help = page.Document.QuerySelector("[data-testid=suspended-help]")!.Plain();
        Assert.Contains("Tus datos se conservan", help);
        Assert.Contains("contacta al equipo de Conversia", help);
        Assert.Empty(page.Select("[data-testid=pending-help]"));
        Assert.DoesNotContain("Falta de pago", page.Body); // the reason is for the administrator, not for the company
    }

    [Fact]
    public async Task A_company_that_operates_is_sent_from_the_pending_page_to_the_dashboard()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        (await session.GetAsync(PendingPage)).ShouldRedirectTo("/panel");
    }

    [Fact]
    public async Task The_pending_page_is_for_signed_in_people_only()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var returnUrl = (await session.GetAsync(PendingPage)).ShouldRedirectToLogin();

        Assert.Equal(PendingPage, returnUrl);
    }

    // ---- the operating pages ----

    [Theory]
    [InlineData("/panel")]
    [InlineData("/conversaciones")]
    [InlineData("/organizacion")]
    [InlineData("/users")]
    [InlineData("/")]
    [InlineData("/conversaciones/9b2f1c3e-0000-4000-8000-000000000001")]
    [InlineData("/organizacion/canales/9b2f1c3e-0000-4000-8000-000000000002/activar")]
    public async Task Every_operating_page_of_a_pending_company_sends_it_to_the_pending_page(string path)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, pending: true);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var response = await session.GetAsync(path);

        response.ShouldRedirectTo(PendingPage);
        var page = await session.FollowAsync(response);
        Assert.Empty(page.Select("[data-testid=error-code]"));
    }

    [Fact]
    public async Task A_company_suspended_while_a_session_is_open_is_sent_to_the_pending_page_and_back_when_reactivated()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        (await session.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);

        await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Suspended");
        (await session.GetAsync("/organizacion")).ShouldRedirectTo(PendingPage);
        (await session.GetAsync("/panel")).ShouldRedirectTo(PendingPage);
        var suspended = (await session.GetAsync(PendingPage)).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("suspendida", suspended.Text);

        await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Active");
        (await session.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);
        (await session.GetAsync(PendingPage)).ShouldRedirectTo("/panel");
    }

    [Fact]
    public async Task Posting_a_form_of_a_company_that_was_suspended_meanwhile_goes_to_the_pending_page()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var users = await WebFlows.UsersAsync(session);
        var form = FormModel.From(users, users.FindPasswordForm()).Set("email", ApiSeed.UniqueEmail("nuevo")).Set("password", "N3w-User-Passw0rd!");

        await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Suspended");
        var response = await session.SubmitAsync(form);

        response.ShouldRedirectTo(PendingPage);
    }

    [Fact]
    public async Task A_permission_change_of_a_company_that_was_suspended_meanwhile_goes_to_the_pending_page()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "miembro");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var users = await WebFlows.UsersAsync(session);
        var grant = FormModel.From(users, users.FindPermissionForm("grant", member.UserId, "UsersView"));

        await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Suspended");
        var response = await session.SubmitAsync(grant);

        response.ShouldRedirectTo(PendingPage);
    }

    [Fact]
    public async Task A_forbidden_answer_without_the_code_is_still_the_forbidden_page()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var nothing = await ApiSeed.CreateMemberAsync(api, owner, "nada");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, nothing.Email);

        var page = (await session.GetAsync("/users")).ShouldBe(HttpStatusCode.Forbidden);

        Assert.NotEmpty(page.Select("[data-page=forbidden]"));
    }

    [Theory]
    [InlineData("Pending", "Tu empresa está pendiente de activación.")]
    [InlineData("Suspended", "Tu empresa está suspendida.")]
    [InlineData(null, "Tu empresa está pendiente de activación.")]
    public async Task A_403_with_the_tenant_not_active_code_from_any_operating_call_is_not_an_error(string? tenantStatus, string detail)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var status = tenantStatus is null ? string.Empty : $",\"tenantStatus\":\"{tenantStatus}\"";
        host.Api.Intercept = request => request.RequestUri!.AbsolutePath is "/api/tenant" or "/api/users/me"
            ? Task.FromResult<HttpResponseMessage?>(null)
            : FakeApi.Respond(HttpStatusCode.Forbidden, $$"""{"status":403,"title":"Empresa no activa","detail":"{{detail}}","code":"tenant_not_active"{{status}}}""");

        foreach (var path in new[] { "/panel", "/conversaciones", "/organizacion", "/users" })
        {
            var response = await session.GetAsync(path);

            response.ShouldRedirectTo(PendingPage);
        }

        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Contains("error", StringComparison.OrdinalIgnoreCase) && entry.Contains("tenant_not_active", StringComparison.Ordinal));
    }

    // ---- the shell ----

    [Fact]
    public async Task The_navigation_of_a_company_that_cannot_operate_locks_the_operating_pages_and_keeps_the_way_out()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, pending: true);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync(PendingPage)).ShouldBe(HttpStatusCode.OK);

        var nav = page.Document.QuerySelector("nav[aria-label='Navegación principal']")!;
        var locked = nav.QuerySelectorAll("[data-locked=true]").ToList();
        Assert.Equal(["Panel", "Conversaciones", "Organización", "Usuarios"], locked.Select(l => l.QuerySelector("span:not(.sr-only)")!.Plain().Trim()));
        Assert.All(locked, l =>
        {
            Assert.Equal("true", l.GetAttribute("aria-disabled")); // locked in words as well as in colour
            Assert.Contains("disponible cuando tu empresa esté activa", l.Plain());
        });
        var links = nav.QuerySelectorAll("a[href]").Select(a => a.GetAttribute("href")).ToList();
        Assert.Equal([PendingPage], links);
        Assert.Equal("page", nav.QuerySelector($"a[href='{PendingPage}']")!.GetAttribute("aria-current"));
        page.FindLogoutForm();
    }

    [Fact]
    public async Task The_navigation_of_a_company_that_operates_is_not_locked()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);

        Assert.Empty(page.Select("[data-locked]"));
        var links = page.Select("nav a[href]").Select(a => a.GetAttribute("href")).ToList();
        Assert.Contains("/panel", links);
        Assert.Contains("/organizacion", links);
    }

    [Fact]
    public async Task The_pending_and_suspended_pages_are_spanish_and_do_not_use_template_wording()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var pending = await ApiSeed.RegisterOwnerAsync(api, "ferretería", pending: true); // test data is Spanish on purpose
        var suspended = await ApiSeed.RegisterOwnerAsync(api, "droguería");
        await ApiSeed.SetStatusAsync(api, admin, suspended.TenantId, "Suspended");
        var report = new SpanishUiReport();
        using var host = WebTestHost.Create(api);
        foreach (var (label, email) in new[] { ("pending", pending.Email), ("suspended", suspended.Email) })
        {
            using var session = await WebFlows.SignedInAsync(host, email);
            report.Check(label, (await session.GetAsync(PendingPage)).ShouldBe(HttpStatusCode.OK));
        }

        report.AssertClean();
    }
}
