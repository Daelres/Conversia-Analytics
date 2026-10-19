extern alias ApiHost;

using System.Net;
using Conversia.Domain.Enums;
using Conversia.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>
/// Requirement "all text shown to users is Spanish", checked over a matrix of real pages and states: each page is fetched once and
/// scanned by <see cref="SpanishUiReport"/> (<c>&lt;html lang="es"&gt;</c>, non-empty <c>&lt;title&gt;</c>, a non-trivial amount of text that
/// contains Spanish, and none of the English words of <c>EnglishLeaks</c> in the visible text or in the user-visible attributes).
/// A failing test lists every leak of every page it visited. Test data is Spanish on purpose.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class SpanishPagesTests(TestApiFactory api)
{
    private const string WrongPassword = "Wr0ng-Pass-Value-123";

    // ---- /login and /register ----

    [Fact]
    public async Task Login_page_is_spanish()
    {
        var report = new SpanishUiReport();
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();
        report.Check("login", (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK));
        report.Check("login with returnUrl", (await session.GetAsync("/login?returnUrl=%2Fusers")).ShouldBe(HttpStatusCode.OK));

        report.AssertClean();
    }

    [Fact]
    public async Task Login_failures_are_spanish_after_401_400_429_and_an_unavailable_backend()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var report = new SpanishUiReport();
        using var host = WebTestHost.Create(api);

        var unauthorized = await WebFlows.LoginAsync(host.NewSession(), owner.Email, WrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, host.Api.Calls.Last().Status); // precondition: this really is the 401 state
        report.Check("login 401", unauthorized);

        var empty = await WebFlows.LoginAsync(host.NewSession(), string.Empty, string.Empty);
        report.Check("login 400 (empty fields)", empty);
        var malformed = await WebFlows.LoginAsync(host.NewSession(), "not-an-email", WrongPassword);
        report.Check("login 400 (malformed email)", malformed);

        host.Api.Intercept = _ => FakeApi.TooManyRequests(37);
        var throttled = await WebFlows.LoginAsync(host.NewSession(), owner.Email, WrongPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, host.Api.Calls.Last().Status); // precondition: the 429 state
        report.Check("login 429", throttled);

        host.Api.Intercept = _ => FakeApi.Respond(HttpStatusCode.TooManyRequests, """{"title":"x","status":429}""");
        report.Check("login 429 without Retry-After", await WebFlows.LoginAsync(host.NewSession(), owner.Email, WrongPassword));

        host.Api.Intercept = FakeApi.Failure("connection refused");
        var unavailable = await WebFlows.LoginAsync(host.NewSession(), owner.Email, WrongPassword);
        Assert.Null(host.Api.Calls.Last().Status); // precondition: the call failed in transport, so this is the "unavailable" state
        report.Check("login unavailable", unavailable);

        report.AssertClean();
    }

    [Fact]
    public async Task Register_page_and_its_409_and_400_states_are_spanish()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var report = new SpanishUiReport();
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        report.Check("register", (await session.GetAsync("/register")).ShouldBe(HttpStatusCode.OK));

        var taken = await WebFlows.RegisterAsync(host.NewSession(), ApiSeed.UniqueTenantName("Ferretería"), owner.Email, ApiSeed.StrongPassword);
        Assert.Equal(HttpStatusCode.Conflict, host.Api.Calls.Last().Status); // precondition: the 409 state
        report.Check("register 409", taken);

        report.Check("register 400 (weak password)", await WebFlows.RegisterAsync(host.NewSession(), ApiSeed.UniqueTenantName("Ferretería"), ApiSeed.UniqueEmail("nuevo"), "corta"));
        report.Check("register 400 (empty fields)", await WebFlows.RegisterAsync(host.NewSession(), string.Empty, string.Empty, string.Empty));
        report.Check("register 400 (malformed email)", await WebFlows.RegisterAsync(host.NewSession(), "Ferretería", "not-an-email", ApiSeed.StrongPassword));

        host.Api.Intercept = FakeApi.Failure("500 problem");
        report.Check("register unavailable", await WebFlows.RegisterAsync(host.NewSession(), "Ferretería", ApiSeed.UniqueEmail("nuevo"), ApiSeed.StrongPassword));

        report.AssertClean();
    }

    // ---- / ----

    [Fact]
    public async Task Home_is_spanish_for_an_anonymous_visitor_an_owner_and_members_with_and_without_permissions()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var viewOnly = await ApiSeed.CreateMemberAsync(api, owner, "solo-lectura", Permission.UsersView);
        var everything = await ApiSeed.CreateMemberAsync(api, owner, "todo", Permission.UsersView, Permission.UsersManage, Permission.ConversationsView, Permission.MetricsView);
        var nothing = await ApiSeed.CreateMemberAsync(api, owner, "nada");
        var report = new SpanishUiReport();
        using var host = WebTestHost.Create(api);

        report.Check("home (anonymous)", (await host.NewSession().GetAsync("/")).ShouldBe(HttpStatusCode.OK));
        foreach (var (label, email) in new[] { ("owner", owner.Email), ("view-only", viewOnly.Email), ("several permissions", everything.Email), ("no permissions", nothing.Email) })
        {
            using var session = await WebFlows.SignedInAsync(host, email);
            var page = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
            Assert.NotNull(page.Document.QuerySelector(PageContract.ProfileTenantId)); // precondition: the profile really rendered
            report.Check($"home ({label})", page);
        }

        using (var failing = await WebFlows.SignedInAsync(host, owner.Email))
        {
            host.Api.Intercept = FakeApi.Failure("500 problem");
            report.Check("home (backend unavailable)", await failing.GetAsync("/"));
        }

        report.AssertClean();
    }

    // ---- /users ----

    [Fact]
    public async Task Users_page_is_spanish_in_every_state_list_create_errors_notices_and_refusals()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var target = await ApiSeed.CreateMemberAsync(api, owner, "objetivo", Permission.UsersView);
        var plain = await ApiSeed.CreateMemberAsync(api, owner, "sin-permisos");
        var viewer = await ApiSeed.CreateMemberAsync(api, owner, "lector", Permission.UsersView);
        var report = new SpanishUiReport();
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var list = await WebFlows.UsersAsync(session);
        Assert.NotEmpty(list.Select(PageContract.UserRow)); // precondition: the user list rendered
        Assert.Contains(list.Document.Forms, f => f.GetAttribute("data-action") is "grant" or "revoke"); // and so did the permission forms
        report.Check("users list (owner)", list);

        const string password = "N3w-User-Passw0rd!";
        async Task<PageResponse> CreateAsync(string email, string pass)
        {
            var page = await WebFlows.UsersAsync(session);
            var form = FormModel.From(page, page.FindPasswordForm()).Set("email", email).Set("password", pass);
            return await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(form));
        }

        report.Check("users create ok (notice)", await CreateAsync(ApiSeed.UniqueEmail("nuevo"), password));
        var duplicate = await CreateAsync(target.Email, password);
        Assert.Equal(HttpStatusCode.Conflict, host.Api.Calls.Last(c => c.Method == HttpMethod.Post).Status); // precondition: the 409 state
        report.Check("users create 409", duplicate);
        report.Check("users create 400 (weak password)", await CreateAsync(ApiSeed.UniqueEmail("debil"), "corta"));
        report.Check("users create 400 (malformed email)", await CreateAsync("not-an-email", password));
        report.Check("users create 400 (empty fields)", await CreateAsync(string.Empty, string.Empty));

        async Task<PageResponse> PermissionAsync(string action, Guid userId, string permission, Action<FormModel>? forge = null)
        {
            var page = await WebFlows.UsersAsync(session);
            var form = FormModel.From(page, page.FindPermissionForm(action, userId, permission));
            forge?.Invoke(form);
            return await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(form));
        }

        report.Check("users grant (notice)", await PermissionAsync("grant", plain.UserId, "UsersView"));
        report.Check("users revoke (notice)", await PermissionAsync("revoke", plain.UserId, "UsersView"));
        report.Check("users grant invalid permission (notice)", await PermissionAsync("grant", plain.UserId, "MetricsView", f => f.Set("Permission", "999")));
        report.Check("users grant unknown user (notice)", await PermissionAsync("grant", plain.UserId, "MetricsView", f => f.Set("UserId", Guid.NewGuid().ToString())));

        var pageBeforeOutage = await WebFlows.UsersAsync(session);
        var actionForm = FormModel.From(pageBeforeOutage, pageBeforeOutage.FindPermissionForm("grant", plain.UserId, "MetricsView"));
        host.Api.Intercept = FakeApi.Failure("500 problem");
        report.Check("users unavailable", await session.GetAsync("/users"));
        report.Check("users action unavailable (notice)", await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(actionForm)));
        host.Api.Intercept = null;

        using (var viewerSession = await WebFlows.SignedInAsync(host, viewer.Email))
        {
            var viewerPage = await WebFlows.UsersAsync(viewerSession);
            Assert.NotEmpty(viewerPage.Select(PageContract.UserRow));
            report.Check("users list (view-only)", viewerPage);

            var ownerPage = await WebFlows.UsersAsync(session);
            var forged = FormModel.From(ownerPage, ownerPage.FindPermissionForm("grant", plain.UserId, "MetricsView"))
                .WithAntiforgeryToken(await WebFlows.AntiforgeryTokenAsync(viewerSession));
            var refused = await WebFlows.FollowRedirectsAsync(viewerSession, await viewerSession.SubmitAsync(forged));
            Assert.Equal(HttpStatusCode.Forbidden, host.Api.Calls.Last(c => c.Method == HttpMethod.Post).Status); // precondition: the API refused
            report.Check("users forged grant refused", refused);
        }

        using (var noPermissions = await WebFlows.SignedInAsync(host, plain.Email))
        {
            var forbidden = await noPermissions.GetAsync("/users");
            forbidden.ShouldBe(HttpStatusCode.Forbidden);
            report.Check("users 403", forbidden);
        }

        report.AssertClean();
    }

    // ---- error pages, antiforgery and logout ----

    [Fact]
    public async Task Not_found_error_and_antiforgery_failure_pages_are_spanish()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var report = new SpanishUiReport();
        using var host = WebTestHost.Create(api);
        using var anonymous = host.NewSession();
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var notFound = await anonymous.GetAsync("/esta-pagina-no-existe");
        notFound.ShouldBe(HttpStatusCode.NotFound);
        report.Check("404 (anonymous)", notFound);
        var notFoundSignedIn = await session.GetAsync("/esta-pagina-no-existe");
        notFoundSignedIn.ShouldBe(HttpStatusCode.NotFound);
        report.Check("404 (signed in)", notFoundSignedIn);
        report.Check("404 (explicit /not-found)", await anonymous.GetAsync("/not-found"));
        report.Check("404 (non-guid conversation id)", await session.GetAsync("/conversaciones/no-es-un-guid"));

        report.Check("error page", await anonymous.GetAsync("/Error"));

        // A rejected login form is an empty 400 (the requirement does not ask for a rendered page): the status and "no cookie" are what is pinned.
        var login = (await anonymous.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
        var rejected = await anonymous.SubmitAsync(FormModel.From(login, login.FindPasswordForm())
            .Set("email", owner.Email).Set("password", ApiSeed.StrongPassword).WithoutAntiforgeryToken());
        rejected.ShouldBe(HttpStatusCode.BadRequest);
        WebFlows.AssertNoCookieIssued(anonymous, rejected);

        report.AssertClean();
    }

    [Fact]
    public async Task The_antiforgery_rejection_of_logout_and_of_the_permission_forms_has_a_spanish_body()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var target = await ApiSeed.CreateMemberAsync(api, owner, "objetivo", Permission.UsersView);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        var users = await WebFlows.UsersAsync(session);

        var rejections = new (string Label, PageResponse Response)[]
        {
            ("logout", await session.SubmitAsync(FormModel.From(home, home.FindLogoutForm()).WithoutAntiforgeryToken())),
            ("grant", await session.SubmitAsync(FormModel.From(users, users.FindPermissionForm("grant", target.UserId, "UsersManage")).WithoutAntiforgeryToken())),
            ("revoke", await session.SubmitAsync(FormModel.From(users, users.FindPermissionForm("revoke", target.UserId, "UsersView")).WithoutAntiforgeryToken())),
        };

        var problems = new List<string>();
        foreach (var (label, response) in rejections)
        {
            if (response.Status != 400)
            {
                problems.Add($"[{label}] expected 400, got {response.Status}.");
            }
            else if (string.IsNullOrWhiteSpace(response.Text))
            {
                problems.Add($"[{label}] the 400 has an empty body; the person gets no explanation.");
            }
            else
            {
                if (!EnglishLeaks.LooksSpanish(response.Text))
                {
                    problems.Add($"[{label}] the body contains no Spanish word: {SpanishUi.Snippet(response.Text)}");
                }

                problems.AddRange(EnglishLeaks.Find(response.Text).Select(leak => $"[{label}] English wording \"{leak}\": {SpanishUi.Snippet(response.Text)}"));
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(session.HasAuthCookie); // nothing was signed out or changed by the rejected posts
    }

    [Fact]
    public async Task An_unhandled_exception_shows_the_spanish_error_page_without_any_exception_text()
    {
        const string secret = "internal-detail-that-must-not-reach-the-page";
        using var host = WebTestHost.Create(api, configureServices: services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(Conversia.Web.Services.IApiClient)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(ThrowingApiClient.Create(secret));
        });
        var report = new SpanishUiReport();

        var failed = await WebFlows.LoginAsync(host.NewSession(), "alguien@example.test", "Wr0ng-Pass-Value-123");

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); // precondition: the stub really made the request blow up
        Assert.DoesNotContain(secret, failed.Body);
        WebFlows.AssertNoStackTrace(failed);
        report.Check("unhandled exception (error page)", failed);
        report.AssertClean();
    }

    [Fact]
    public async Task Logout_flow_is_spanish_on_every_page_it_passes_through()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var report = new SpanishUiReport();
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        report.Check("home before logout", home);
        report.Check("GET /logout (does nothing)", await session.GetAsync("/logout"));

        var response = await session.SubmitAsync(FormModel.From(home, home.FindLogoutForm()));
        Assert.True(response.IsRedirect, $"Logout should redirect, got {response.Status}.");
        Assert.False(session.HasAuthCookie); // precondition: the user is really signed out
        report.Check("page after logout", await WebFlows.FollowRedirectsAsync(session, response));

        var guarded = await session.GetAsync("/users");
        guarded.ShouldRedirectToLogin();
        report.Check("login after the redirect from a guarded page", await session.FollowAsync(guarded));

        report.AssertClean();
    }

    /// <summary>Stands in for the API client and fails the way a bug would (not a transport error, which the real client turns into "unavailable"). A proxy, so a new API call needs no change here.</summary>
    public class ThrowingApiClient : System.Reflection.DispatchProxy
    {
        public string Secret { get; set; } = string.Empty;

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException(Secret);

        public static Conversia.Web.Services.IApiClient Create(string secret)
        {
            var proxy = Create<Conversia.Web.Services.IApiClient, ThrowingApiClient>();
            ((ThrowingApiClient)(object)proxy).Secret = secret;
            return proxy;
        }
    }
}
