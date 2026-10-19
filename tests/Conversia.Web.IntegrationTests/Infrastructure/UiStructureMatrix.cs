using System.Net;
using System.Reflection;
using Conversia.Domain.Enums;
using Conversia.TestSupport;
using Conversia.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// Every page and state the structure rules run over, fetched ONCE (the first test that asks builds it; the others reuse it) and
/// buffered, so the hosts can be disposed and no rule re-fetches anything. The matrix is the one of the "all text is Spanish" suite
/// (<c>SpanishPagesTests</c>) plus the states of the error answers: anonymous and signed-in visitors, an owner, a view-only member,
/// members without permissions, the users page and its rejections, the login throttled by the API (429), the 403/404/error pages and the
/// HTML-labelled 400 answer of a rejected antiforgery token (an empty body today, see below). The assets the pages link (and the sheets
/// those import) are fetched once through the same host.
/// </summary>
internal sealed class UiStructureMatrix
{
    private const string WrongPassword = "Wr0ng-Pass-Value-123";
    private const string NewUserPassword = "N3w-User-Passw0rd!";

    private static readonly object Gate = new();
    private static Task<UiStructureMatrix>? _instance;

    private UiStructureMatrix(IReadOnlyList<UiPageCase> cases, UiAssets assets, IReadOnlyDictionary<string, PageResponse> legacyBootstrap)
    {
        Cases = cases;
        Assets = assets;
        LegacyBootstrap = legacyBootstrap;
    }

    public IReadOnlyList<UiPageCase> Cases { get; }

    public UiAssets Assets { get; }

    /// <summary>The Bootstrap files the template used to serve, by path, as the site answers them now.</summary>
    public IReadOnlyDictionary<string, PageResponse> LegacyBootstrap { get; }

    public static Task<UiStructureMatrix> GetAsync(TestApiFactory api)
    {
        lock (Gate)
        {
            return _instance ??= BuildAsync(api);
        }
    }

    private static async Task<UiStructureMatrix> BuildAsync(TestApiFactory api)
    {
        var cases = new List<UiPageCase>();

        // The files the pages link are fetched while a host is alive (the static files of the Web are the same on both hosts).
        Dictionary<Uri, UiAsset> files = new();
        Dictionary<string, PageResponse> legacy = new();

        using (var host = WebTestHost.Create(api))
        {
            using var anonymous = host.NewSession();
            await AddAnonymousPagesAsync(api, host, cases);
            await AddSignedInPagesAsync(api, host, cases);
            await AddMembersAsync(api, host, cases);

            // The real error page: an unhandled exception in a page, answered by the exception handler with a 500.
            using (var broken = WebTestHost.Create(api, configureServices: services =>
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IApiClient)).ToList())
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton(DispatchProxy.Create<IApiClient, ThrowingApiClient>());
            }))
            {
                var failed = await LoginAsync(broken, "alguien@example.test", WrongPassword);
                Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); // precondition: the exception handler answered
                cases.Add(Case("unhandled exception (error page)", failed, UiPageType.Error, authenticated: false, submitted: true));
            }

            // Each file once; a sheet may import others, which are pages' CSS too.
            var pending = new Queue<Uri>(cases.SelectMany(c => UiStructureAssertions.LinkedFiles(c.Page)).Distinct());
            while (pending.TryDequeue(out var url))
            {
                if (files.ContainsKey(url))
                {
                    continue;
                }

                var (response, bytes) = await anonymous.GetFileAsync(url.PathAndQuery);
                files[url] = new UiAsset(response, bytes);
                if (response.Status == 200 && response.Header("Content-Type")?.StartsWith("text/css", StringComparison.OrdinalIgnoreCase) == true)
                {
                    foreach (var import in UiStructureAssertions.CssImports(response.Body, url))
                    {
                        pending.Enqueue(import);
                    }
                }
            }

            foreach (var path in new[] { "/lib/bootstrap/dist/css/bootstrap.min.css", "/lib/bootstrap/dist/js/bootstrap.bundle.min.js" })
            {
                legacy[path] = await anonymous.GetAsync(path);
            }
        }

        return new UiStructureMatrix(cases, new UiAssets(files), legacy);
    }

    // ---- anonymous visitors ----

    private static async Task AddAnonymousPagesAsync(TestApiFactory api, WebTestHost host, List<UiPageCase> cases)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var anonymous = host.NewSession();

        cases.Add(Case("anonymous /login", await Get(anonymous, "/login"), UiPageType.Login, false));
        cases.Add(Case("anonymous /login?returnUrl", await Get(anonymous, "/login?returnUrl=%2Fusers"), UiPageType.Login, false));

        var malformed = await LoginAsync(host, "not-an-email", WrongPassword);
        cases.Add(Case("login rejected locally (malformed email)", malformed, UiPageType.Login, false, submitted: true, invalidFields: ["email"]));
        var empty = await LoginAsync(host, string.Empty, string.Empty);
        cases.Add(Case("login rejected locally (empty fields)", empty, UiPageType.Login, false, submitted: true, invalidFields: ["email", "password"]));

        var unauthorized = await LoginAsync(host, owner.Email, WrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, host.Api.Calls.Last().Status); // precondition: the 401 state
        cases.Add(Case("login 401", unauthorized, UiPageType.Login, false, submitted: true, alert: true));

        host.Api.Intercept = FakeApi.Failure("connection refused");
        var unavailable = await LoginAsync(host, owner.Email, WrongPassword);
        host.Api.Intercept = null;
        cases.Add(Case("login backend unavailable", unavailable, UiPageType.Login, false, submitted: true, alert: true));

        host.Api.Intercept = _ => FakeApi.TooManyRequests(37);
        var throttled = await LoginAsync(host, owner.Email, WrongPassword);
        host.Api.Intercept = null;
        Assert.Equal(HttpStatusCode.TooManyRequests, host.Api.Calls.Last().Status); // precondition: the 429 state
        Assert.Equal(HttpStatusCode.OK, throttled.StatusCode); // the Web re-renders the sign-in form with a 200
        cases.Add(Case("login 429 (rate limited)", throttled, UiPageType.Login, false, submitted: true, alert: true));

        cases.Add(Case("anonymous /register", await Get(anonymous, "/register"), UiPageType.Register, false));
        var registerEmpty = await RegisterAsync(host, string.Empty, string.Empty, string.Empty);
        cases.Add(Case("register rejected (empty fields)", registerEmpty, UiPageType.Register, false, submitted: true, invalidFields: ["tenantName", "email", "password"]));
        var weak = await RegisterAsync(host, ApiSeed.UniqueTenantName("Ferretería"), ApiSeed.UniqueEmail("nuevo"), "corta");
        cases.Add(Case("register rejected (weak password)", weak, UiPageType.Register, false, submitted: true, invalidFields: ["password"]));
        var taken = await RegisterAsync(host, ApiSeed.UniqueTenantName("Ferretería"), owner.Email, ApiSeed.StrongPassword);
        Assert.Equal(HttpStatusCode.Conflict, host.Api.Calls.Last().Status); // precondition: the 409 state
        cases.Add(Case("register 409 (email taken)", taken, UiPageType.Register, false, submitted: true, alert: true));

        cases.Add(Case("anonymous /", await Get(anonymous, "/"), UiPageType.Home, false));
        cases.Add(Case("anonymous 404", await Get(anonymous, "/esta-pagina-no-existe", HttpStatusCode.NotFound), UiPageType.NotFound, false));
        cases.Add(Case("anonymous /not-found", await Get(anonymous, "/not-found"), UiPageType.NotFound, false));
        cases.Add(Case("anonymous /Error", await Get(anonymous, "/Error"), UiPageType.Error, false));

        // A rejected antiforgery token. Today the answer is an EMPTY body that still says Content-Type: text/html (which is why the status-code
        // re-execution never fires), so it is not a page yet. The pinned requirement is an "error" page about an invalid or expired request,
        // which needs a new production handler; until then the rules on these cases are red for that design-missing reason.
        using var tampered = host.NewSession();
        var loginPage = await Get(tampered, "/login");
        var withoutToken = FormModel.From(loginPage, loginPage.FindPasswordForm()).Set("email", owner.Email).Set("password", WrongPassword).WithoutAntiforgeryToken();
        var antiforgery = await tampered.SubmitAsync(withoutToken);
        Assert.Equal(HttpStatusCode.BadRequest, antiforgery.StatusCode); // precondition: the antiforgery 400 state
        Assert.Contains("text/html", antiforgery.Header("Content-Type"), StringComparison.OrdinalIgnoreCase); // precondition: the answer is labelled HTML (an empty body today), not plain text
        cases.Add(Case("anonymous login post without antiforgery token (400)", antiforgery, UiPageType.Error, false, submitted: true));
    }

    // ---- an owner, through every page and rejection ----

    private static async Task AddSignedInPagesAsync(TestApiFactory api, WebTestHost host, List<UiPageCase> cases)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, "ferreteria");
        var plainMember = await ApiSeed.CreateMemberAsync(api, owner, "sin-permisos");
        var existingMember = await ApiSeed.CreateMemberAsync(api, owner, "existente", Permission.UsersView);

        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var home = await Get(session, "/");
        Assert.NotNull(home.Document.QuerySelector(PageContract.ProfileTenantId)); // precondition: the profile rendered
        cases.Add(Case("owner /", home, UiPageType.Home, true));

        var users = await Get(session, "/users");
        Assert.NotEmpty(users.Select(PageContract.UserRow));
        cases.Add(Case("owner /users", users, UiPageType.Users, true));

        cases.Add(Case("owner 404", await Get(session, "/esta-pagina-no-existe", HttpStatusCode.NotFound), UiPageType.NotFound, true));
        cases.Add(Case("owner /Error", await Get(session, "/Error"), UiPageType.Error, true));

        // ---- user forms rejected and confirmed ----

        async Task<PageResponse> CreateUserAsync(string email, string password)
        {
            var page = await WebFlows.UsersAsync(session);
            var form = FormModel.From(page, page.FindPasswordForm()).Set("email", email).Set("password", password);
            return await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(form));
        }

        cases.Add(Case("users create rejected (malformed email)", await CreateUserAsync("not-an-email", NewUserPassword), UiPageType.Users, true, submitted: true, invalidFields: ["email"]));
        cases.Add(Case("users create rejected (weak password)", await CreateUserAsync(ApiSeed.UniqueEmail("debil"), "corta"), UiPageType.Users, true, submitted: true, invalidFields: ["password"]));
        var duplicateUser = await CreateUserAsync(existingMember.Email, NewUserPassword);
        Assert.Equal(HttpStatusCode.Conflict, host.Api.Calls.Last(c => c.Method == HttpMethod.Post).Status); // precondition: the 409 state
        cases.Add(Case("users create 409 (email taken)", duplicateUser, UiPageType.Users, true, submitted: true, alert: true));
        var createdUser = await CreateUserAsync(ApiSeed.UniqueEmail("nuevo"), NewUserPassword);
        Assert.Contains("notice=created", createdUser.RequestUri.Query); // precondition: the confirmation page
        cases.Add(Case("users create confirmed", createdUser, UiPageType.Users, true, submitted: true, status: true));

        var usersPage = await WebFlows.UsersAsync(session);
        var grant = FormModel.From(usersPage, usersPage.FindPermissionForm("grant", plainMember.UserId, "UsersView"));
        var granted = await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(grant));
        Assert.Contains("notice=granted", granted.RequestUri.Query);
        cases.Add(Case("users grant confirmed", granted, UiPageType.Users, true, submitted: true, status: true));

        var forgedPage = await WebFlows.UsersAsync(session);
        var forged = FormModel.From(forgedPage, forgedPage.FindPasswordForm()).Set("email", ApiSeed.UniqueEmail("sin-token")).Set("password", NewUserPassword).WithAntiforgeryToken("CfDJ8-this-is-not-a-valid-token");
        var antiforgery = await session.SubmitAsync(forged);
        Assert.Equal(HttpStatusCode.BadRequest, antiforgery.StatusCode); // precondition: the antiforgery 400 state
        Assert.Contains("text/html", antiforgery.Header("Content-Type"), StringComparison.OrdinalIgnoreCase); // precondition: the answer is labelled HTML (an empty body today), not plain text
        cases.Add(Case("owner users post with an invalid antiforgery token (400)", antiforgery, UiPageType.Error, true, submitted: true));

        // ---- the backend is down ----

        host.Api.Intercept = FakeApi.Failure("500 problem");
        cases.Add(Case("owner /users backend unavailable", await Get(session, "/users", HttpStatusCode.ServiceUnavailable), UiPageType.Users, true, alert: true));
        cases.Add(Case("owner / backend unavailable", await Get(session, "/", HttpStatusCode.ServiceUnavailable), UiPageType.Home, true, alert: true));
        host.Api.Intercept = null;
    }

    // ---- members with fewer rights ----

    private static async Task AddMembersAsync(TestApiFactory api, WebTestHost host, List<UiPageCase> cases)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, "miembros");
        var usersViewer = await ApiSeed.CreateMemberAsync(api, owner, "usuarios", Permission.UsersView);
        var nothing = await ApiSeed.CreateMemberAsync(api, owner, "nada");
        var everything = await ApiSeed.CreateMemberAsync(api, owner, "todo", Permission.UsersView, Permission.UsersManage, Permission.OrganizationView, Permission.OrganizationManage);

        using (var session = await WebFlows.SignedInAsync(host, usersViewer.Email))
        {
            cases.Add(Case("view-only /", await Get(session, "/"), UiPageType.Home, true));
            var users = await Get(session, "/users");
            Assert.NotEmpty(users.Select(PageContract.UserRow));
            cases.Add(Case("users-view-only /users", users, UiPageType.Users, true));
        }

        using (var session = await WebFlows.SignedInAsync(host, nothing.Email))
        {
            cases.Add(Case("no-permission /", await Get(session, "/"), UiPageType.Home, true));
            cases.Add(Case("no-permission /users", await Get(session, "/users", HttpStatusCode.Forbidden), UiPageType.Forbidden, true));
            cases.Add(Case("no-permission /Error", await Get(session, "/Error"), UiPageType.Error, true));
        }

        using (var session = await WebFlows.SignedInAsync(host, everything.Email))
        {
            var home = await Get(session, "/");
            Assert.Equal(4, home.Select(PageContract.ProfilePermission).Count()); // precondition: all four permissions are listed
            cases.Add(Case("all-permissions member /", home, UiPageType.Home, true));
        }
    }

    // ---- building blocks ----

    /// <summary>An API client that fails like a bug would (not a transport error, which the real client turns into "unavailable").</summary>
    public class ThrowingApiClient : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("internal-detail-that-must-not-reach-the-page");
    }

    /// <summary>A page fetched with a GET, which must answer with the expected status (a precondition: only then is the rest of the page meaningful).</summary>
    private static async Task<PageResponse> Get(WebSession session, string url, HttpStatusCode expected = HttpStatusCode.OK)
        => (await session.GetAsync(url)).ShouldBe(expected);

    /// <summary>A page 403 is the forbidden page, a 404 the not-found page and an HTML 400 (antiforgery) an error page, whatever route they answer.</summary>
    private static UiPageType Resolved(PageResponse page, UiPageType routeType) => page.Status switch
    {
        403 => UiPageType.Forbidden,
        404 => UiPageType.NotFound,
        400 when page.Header("Content-Type")?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true => UiPageType.Error,
        _ => routeType,
    };

    /// <summary>A sign-in through a throw-away browser tab, disposed once the page is buffered.</summary>
    private static async Task<PageResponse> LoginAsync(WebTestHost host, string email, string password)
    {
        using var session = host.NewSession();
        return await WebFlows.LoginAsync(session, email, password);
    }

    private static async Task<PageResponse> RegisterAsync(WebTestHost host, string tenantName, string email, string password)
    {
        using var session = host.NewSession();
        return await WebFlows.RegisterAsync(session, tenantName, email, password);
    }

    private static UiPageCase Case(
        string label, PageResponse page, UiPageType type, bool authenticated,
        bool submitted = false, bool alert = false, bool status = false, string[]? invalidFields = null)
        => new()
        {
            Label = label,
            Page = page,
            Type = Resolved(page, type),
            Authenticated = authenticated,
            Submitted = submitted,
            ExpectAlert = alert,
            ExpectStatus = status,
            InvalidFields = invalidFields ?? [],
        };
}
