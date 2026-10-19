using System.Net;
using Conversia.Web.Services;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// The ways a request can end in an unhandled exception of the Web, by name: a page read, an enhanced navigation (the fetch the
/// browser makes when it follows a link), and every kind of form post. Each one is "an ordinary request, made while the Web is
/// broken" (<see cref="WebScenario.BreakTheWeb"/>), so the page that answers it is the Web's own exception handling.
/// </summary>
internal static class UnhandledScenarios
{
    public const string EnhancedNavigationAccept = "text/html; blazor-enhanced-nav=on";

    public static readonly IReadOnlyList<string> Names =
    [
        "GET /", "GET /users", "GET /conversaciones", "GET /conversaciones/{id}", "GET /organizacion",
        "enhanced GET /users", "enhanced GET /conversaciones",
        "POST /login", "POST /register", "POST /users", "POST /organizacion", "POST /conversaciones/{id}",
    ];

    public static TheoryData<string> All
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Names)
            {
                data.Add(name);
            }

            return data;
        }
    }

    /// <summary>The pages of the scenarios that are plain reads: a smaller axis for the tests that need many hosts.</summary>
    public static TheoryData<string> Representative => new() { "GET /users", "POST /login", "enhanced GET /conversaciones", "POST /organizacion" };

    public static string PathOf(string scenario, Guid conversationId)
        => scenario.Split(' ')[^1].Replace("{id}", conversationId.ToString());

    /// <summary>The scenarios that are a plain page read or a form post (no enhanced navigation): the pages a person can fail on.</summary>
    public static TheoryData<string> Pages
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Names.Where(n => !n.StartsWith("enhanced", StringComparison.Ordinal)))
            {
                data.Add(name);
            }

            return data;
        }
    }

    /// <summary>
    /// Performs the named request after breaking the Web (or, with <paramref name="breakIt"/>, after breaking something else, say the
    /// API) and returns the page it got. Pages and forms that need data are fetched first, while everything still works; the
    /// signed-in scenarios use <paramref name="email"/>.
    /// </summary>
    public static async Task<PageResponse> TriggerAsync(
        WebScenario scenario, string name, string email = "owner@example.test", Guid? conversationId = null, WebSession? existing = null, Action<WebScenario>? breakIt = null)
    {
        void Break() => (breakIt ?? (s => s.BreakTheWeb()))(scenario);

        var id = conversationId ?? ConversationsStub.Waiting;
        var anonymous = name is "POST /login" or "POST /register";
        var session = existing ?? (anonymous ? scenario.Anonymous() : await scenario.SignInAsync(email));

        switch (name)
        {
            case "POST /login":
            {
                var page = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
                var form = FormModel.From(page, page.FindPasswordForm()).Set("email", "ana@example.test").Set("password", WebScenario.Password);
                Break();
                return await session.SubmitAsync(form);
            }

            case "POST /register":
            {
                var page = (await session.GetAsync("/register")).ShouldBe(HttpStatusCode.OK);
                var form = FormModel.From(page, page.FindPasswordForm())
                    .Set("tenantName", "Acme").Set("email", "ana@example.test").Set("password", WebScenario.Password);
                Break();
                return await session.SubmitAsync(form);
            }

            case "POST /users":
            {
                var page = await WebFlows.UsersAsync(session);
                var form = FormModel.From(page, page.FindPasswordForm()).Set("email", "new@example.test").Set("password", WebScenario.Password);
                Break();
                return await session.SubmitAsync(form);
            }

            case "POST /organizacion":
            {
                var page = (await session.GetAsync("/organizacion")).ShouldBe(HttpStatusCode.OK);
                var form = FormModel.From(page, page.FindForm(f => f.GetAttribute("data-testid") == "branch-form", "branch-form")).Set("Name", "Poblado");
                Break();
                return await session.SubmitAsync(form);
            }

            case "POST /conversaciones/{id}":
            {
                var page = (await session.GetAsync(PathOf(name, id))).ShouldBe(HttpStatusCode.OK);
                var form = FormModel.From(page, page.FindForm(f => f.GetAttribute("data-testid") == "assign-form", "assign-form"));
                Break();
                return await session.SubmitAsync(form);
            }

            case "enhanced GET /users":
            case "enhanced GET /conversaciones":
                Break();
                return await session.GetAsync(PathOf(name, id), ("Accept", EnhancedNavigationAccept));

            default:
                Break();
                return await session.GetAsync(PathOf(name, id));
        }
    }
}
