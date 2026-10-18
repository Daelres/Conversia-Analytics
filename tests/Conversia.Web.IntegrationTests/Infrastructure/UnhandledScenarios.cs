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
        "GET /", "GET /users", "GET /products", "GET /products/{id}/edit", "GET /products/{id}/delete", "GET /demo",
        "enhanced GET /users", "enhanced GET /products",
        "POST /login", "POST /register", "POST /users", "POST /products", "POST /products/{id}/edit", "POST /products/{id}/delete",
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
    public static TheoryData<string> Representative => new() { "GET /users", "POST /login", "enhanced GET /products", "POST /products" };

    public static string PathOf(string scenario, Guid productId)
        => scenario.Split(' ')[^1].Replace("{id}", productId.ToString());

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
        WebScenario scenario, string name, string email = "owner@example.test", Guid? productId = null, WebSession? existing = null, Action<WebScenario>? breakIt = null)
    {
        void Break() => (breakIt ?? (s => s.BreakTheWeb()))(scenario);

        var id = productId ?? Guid.NewGuid();
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

            case "POST /products":
            {
                var (_, form) = await ProductFlows.OpenCreateFormAsync(session);
                form = ProductFlows.FillCreate(form, "NEW-1", "Yunque", "10,50", "3");
                Break();
                return await session.SubmitAsync(form);
            }

            case "POST /products/{id}/edit":
            {
                var (_, form) = await ProductFlows.OpenEditFormAsync(session, id);
                form = ProductFlows.FillEdit(form, "Yunque 2", "11,00", "4");
                Break();
                return await session.SubmitAsync(form);
            }

            case "POST /products/{id}/delete":
            {
                var (_, form) = await ProductFlows.OpenDeleteFormAsync(session, id);
                Break();
                return await session.SubmitAsync(form);
            }

            case "enhanced GET /users":
            case "enhanced GET /products":
                Break();
                return await session.GetAsync(PathOf(name, id), ("Accept", EnhancedNavigationAccept));

            default:
                Break();
                return await session.GetAsync(PathOf(name, id));
        }
    }
}
