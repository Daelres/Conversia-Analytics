extern alias ApiHost;

using System.Net;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Security;

[Collection(nameof(WebIntegrationCollection))]
public class SecurityTests(TestApiFactory api)
{
    private const string ExceptionMessage = "boom-secret-detail-4711";

    [Fact]
    public async Task A_whole_journey_never_shows_the_jwt_in_a_response_cookie_or_log()
    {
        var email = ApiSeed.UniqueEmail("journey");
        var memberEmail = ApiSeed.UniqueEmail("journey-member");
        const string memberPassword = "J0urney-Member-Pw!";
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        (await WebFlows.RegisterAsync(session, ApiSeed.UniqueTenantName("journey"), email, ApiSeed.StrongPassword)).ShouldRedirectTo("/panel");
        (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        var users = await WebFlows.UsersAsync(session);
        var create = FormModel.From(users, users.FindPasswordForm()).Set("email", memberEmail).Set("password", memberPassword);
        var created = await session.SubmitAsync(create);
        users = await WebFlows.UsersAsync(session);
        var memberId = Assert.Single(users.Select(PageContract.UserRow), r => r.TextContent.Contains(memberEmail)).GetAttribute("data-user-id")!;
        await session.SubmitAsync(FormModel.From(users, users.FindPermissionForm("grant", Guid.Parse(memberId), "UsersView")));
        var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        await session.SubmitAsync(FormModel.From(home, home.FindLogoutForm()));

        Assert.True(created.Status is 200 or 302 or 303);
        Assert.NotEmpty(host.Api.IssuedTokens);
        Assert.Contains(session.Transcript, r => r.RequestUri.AbsolutePath == "/users" && r.Body.Contains(memberEmail));
        WebFlows.AssertNoJwtLeaked(session, host);
        WebFlows.AssertNoSecretLogged(host, ApiSeed.StrongPassword, memberPassword);
    }

    public static TheoryData<string> Pages => new() { "login", "register", "home", "users", "create-user" };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task An_unexpected_exception_never_reaches_the_browser_as_a_stack_trace(string page)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = page is "login" or "register" ? host.NewSession() : await WebFlows.SignedInAsync(host, owner.Email);
        host.Api.Intercept = _ => throw new InvalidOperationException(ExceptionMessage);

        var response = page switch
        {
            "login" => await WebFlows.LoginAsync(session, owner.Email, ApiSeed.StrongPassword),
            "register" => await WebFlows.RegisterAsync(session, ApiSeed.UniqueTenantName("boom"), ApiSeed.UniqueEmail("boom"), ApiSeed.StrongPassword),
            "home" => await session.GetAsync("/"),
            "users" => await session.GetAsync("/users"),
            _ => await CreateUserWithFailingApiAsync(session, host)
        };

        Assert.Contains(host.Api.Calls, c => c.Status is null); // a call reached the failing API
        Assert.Contains((int)response.StatusCode, new[] { 200, 500, 503 });
        AssertIsNonEmptyHtmlPage(response); // a bare 500 with no body is not a "controlled" outcome
        Assert.DoesNotContain(ExceptionMessage, response.Body);
        Assert.DoesNotContain("InvalidOperationException", response.Body);
        WebFlows.AssertNoStackTrace(response);
        Assert.False(PageResponse.ContainsJwt(response.Body));
        if (page is "login" or "register")
        {
            Assert.False(session.HasAuthCookie);
        }
    }

    [Fact]
    public async Task The_error_page_itself_shows_no_exception_detail()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await session.GetAsync("/Error");

        response.ShouldBe(HttpStatusCode.OK);
        AssertIsNonEmptyHtmlPage(response);
        WebFlows.AssertNoStackTrace(response);
        Assert.DoesNotContain(ExceptionMessage, response.Body);
        Assert.False(PageResponse.ContainsJwt(response.Body));
        Assert.Equal(0, host.Api.CallCount);
    }

    [Fact]
    public async Task Authenticated_pages_that_carry_tenant_data_are_not_cacheable()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        WebFlows.AssertNotStored((await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK));
        WebFlows.AssertNotStored((await session.GetAsync("/users")).ShouldBe(HttpStatusCode.OK));
    }

    [Fact]
    public async Task Auth_pages_and_their_failures_are_not_cacheable()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        WebFlows.AssertNotCacheable((await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK));
        WebFlows.AssertNotCacheable((await session.GetAsync("/register")).ShouldBe(HttpStatusCode.OK));
        WebFlows.AssertNotCacheable(await WebFlows.LoginAsync(session, ApiSeed.UniqueEmail("nobody"), "Wr0ng-Pass-Value-123"));
        WebFlows.AssertNotCacheable(await WebFlows.RegisterAsync(session, "", "not-an-email", "short"));
    }

    private static void AssertIsNonEmptyHtmlPage(PageResponse response)
    {
        Assert.Equal("text/html", response.Header("Content-Type")?.Split(';')[0]);
        Assert.False(string.IsNullOrWhiteSpace(response.Text), $"The {response.Status} response for {response.RequestUri.PathAndQuery} has an empty body.");
    }

    private static async Task<PageResponse> CreateUserWithFailingApiAsync(WebSession session, WebTestHost host)
    {
        // The users page itself needs the API, so build the form first, then break the API.
        host.Api.Intercept = null;
        var page = await WebFlows.UsersAsync(session);
        var form = FormModel.From(page, page.FindPasswordForm())
            .Set("email", ApiSeed.UniqueEmail("boom")).Set("password", ApiSeed.StrongPassword);
        host.Api.Intercept = _ => throw new InvalidOperationException(ExceptionMessage);
        return await session.SubmitAsync(form);
    }
}
