extern alias ApiHost;

using System.Net;
using System.Text.Json;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>GET/POST /login: static SSR form over POST /api/auth/login, cookie sign-in, controlled failures.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class LoginPageTests(TestApiFactory api)
{
    private const string WrongPassword = "Wr0ng-Pass-Value-123";

    [Fact]
    public async Task Get_login_renders_an_uncacheable_form_with_email_password_and_antiforgery_token()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("text/html", page.Header("Content-Type")?.Split(';')[0]);
        var form = FormModel.From(page, page.FindPasswordForm());
        Assert.NotNull(form.Get("email"));
        Assert.NotNull(form.Get("password"));
        Assert.False(string.IsNullOrEmpty(form.Get(FormModel.AntiforgeryField)), "The form must carry an antiforgery token.");
        WebFlows.AssertNotCacheable(page);
        Assert.Equal(0, host.Api.CallCount);
    }

    [Fact]
    public async Task Successful_login_signs_in_with_a_hardened_cookie_and_redirects_home()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, owner.Email, ApiSeed.StrongPassword);

        response.ShouldRedirectTo("/");
        WebFlows.AssertNotCacheable(response);

        // A profile fetch after sign-in would be fine; the login POST itself must happen exactly once.
        var call = Assert.Single(host.Api.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery == "/api/auth/login");
        Assert.Equal("http://api.test/api/auth/login", call.Uri.GetLeftPart(UriPartial.Path));
        using (var sent = JsonDocument.Parse(call.Body!))
        {
            Assert.Equal(owner.Email, sent.RootElement.GetProperty("email").GetString());
        }

        var token = Assert.Single(host.Api.IssuedTokens);
        var cookies = WebFlows.IssuedCookies(response);
        Assert.NotEmpty(cookies); // a large ticket may be chunked into several cookies
        Assert.All(cookies, cookie => AssertHardenedCookie(cookie, token));
        Assert.True(session.HasAuthCookie);

        // The session really works: the Web replays the JWT it kept server-side.
        var users = await WebFlows.UsersAsync(session);
        Assert.Contains(owner.Email, users.Text);
        Assert.Equal($"Bearer {token}", host.Api.Calls.Last(c => c.PathAndQuery == "/api/users").Authorization);

        WebFlows.AssertNoJwtLeaked(session, host);
        WebFlows.AssertNoSecretLogged(host, ApiSeed.StrongPassword);
    }

    [Theory]
    [InlineData("/users", "/users")]
    [InlineData("/users?page=2", "/users?page=2")]
    [InlineData("/", "/")]
    public async Task Login_redirects_to_a_local_return_url(string returnUrl, string expectedLocation)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, owner.Email, ApiSeed.StrongPassword, returnUrl);

        response.ShouldRedirectTo(expectedLocation);
        Assert.True(session.HasAuthCookie);
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("///evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("\\evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData(" //evil.example")]
    [InlineData("https://evil.example/")]
    [InlineData("http://localhost/users")]
    [InlineData("javascript:alert(1)")]
    [InlineData("evil.example")]
    [InlineData("users")]
    [InlineData("")]
    public async Task Login_never_redirects_to_a_non_local_return_url(string returnUrl)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, owner.Email, ApiSeed.StrongPassword, returnUrl);

        response.ShouldRedirectTo("/");
        Assert.True(session.HasAuthCookie);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_show_the_same_generic_message_without_a_cookie()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var unknownEmail = ApiSeed.UniqueEmail("nobody");
        using var host = WebTestHost.Create(api);

        var outcomes = new List<(string Email, PageResponse Response)>();
        foreach (var email in new[] { owner.Email, unknownEmail })
        {
            using var session = host.NewSession();
            var response = await WebFlows.LoginAsync(session, email, WrongPassword);

            response.ShouldBe(HttpStatusCode.OK);
            Assert.Contains(SpanishText.InvalidCredentials, response.Text);
            WebFlows.AssertNoCookieIssued(session, response);
            WebFlows.AssertCleanFailurePage(response, WrongPassword);
            WebFlows.AssertNotCacheable(response);
            var form = FormModel.From(response, response.FindPasswordForm());
            Assert.Equal(email, form.Get("email"));
            outcomes.Add((email, response));
        }

        Assert.Equal(
            outcomes[0].Response.Text.Replace(outcomes[0].Email, "<email>"),
            outcomes[1].Response.Text.Replace(outcomes[1].Email, "<email>"));
        Assert.Equal(2, host.Api.Calls.Count(c => c.Status == HttpStatusCode.Unauthorized));
        WebFlows.AssertNoSecretLogged(host, WrongPassword);
    }

    [Fact]
    public async Task Failed_login_page_encodes_the_echoed_email()
    {
        const string hostile = "\"><script>alert(1)</script>@example.test";
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, hostile, WrongPassword);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.DoesNotContain("<script>alert(1)", response.Body);
        Assert.DoesNotContain(response.Document.Scripts, s => s.TextContent.Contains("alert(1)"));
        Assert.Equal(hostile, FormModel.From(response, response.FindPasswordForm()).Get("email"));
        WebFlows.AssertNoCookieIssued(session, response);
    }

    [Fact]
    public async Task Api_validation_errors_are_shown_per_field_and_nothing_is_signed_in()
    {
        using var host = WebTestHost.Create(api);
        host.Api.Intercept = request => FakeApi.IsPath(request, "/api/auth/login")
            ? FakeApi.Validation(("Email", "Stub says: email is not valid."), ("Password", "Stub says: password is required."))
            : Task.FromResult<HttpResponseMessage?>(null);
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, "someone@example.test", WrongPassword);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Contains("Stub says: email is not valid.", response.Text);
        Assert.Contains("Stub says: password is required.", response.Text);
        Assert.Equal(1, host.Api.CallCount);
        WebFlows.AssertNoCookieIssued(session, response);
        WebFlows.AssertCleanFailurePage(response, WrongPassword);
        Assert.Equal("someone@example.test", FormModel.From(response, response.FindPasswordForm()).Get("email"));
    }

    [Fact]
    public async Task Api_rate_limit_is_reported_with_the_retry_after_seconds()
    {
        using var host = WebTestHost.Create(api);
        host.Api.Intercept = _ => FakeApi.TooManyRequests(37);
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, "someone@example.test", WrongPassword);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Contains(SpanishText.TooManyAttempts, response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("37", response.Text);
        WebFlows.AssertNoCookieIssued(session, response);
        WebFlows.AssertCleanFailurePage(response, WrongPassword);
        Assert.Equal("someone@example.test", FormModel.From(response, response.FindPasswordForm()).Get("email"));
    }

    [Fact]
    public async Task Real_api_throttling_surfaces_as_too_many_attempts_with_the_real_retry_after()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var throttledApi = api.WithLoginRateLimit(permitLimit: 1, windowSeconds: 3600);
        using var host = WebTestHost.Create(api, throttledApi);

        using var first = host.NewSession();
        (await WebFlows.LoginAsync(first, owner.Email, ApiSeed.StrongPassword)).ShouldRedirectTo("/");

        using var second = host.NewSession();
        var throttled = await WebFlows.LoginAsync(second, owner.Email, ApiSeed.StrongPassword);

        throttled.ShouldBe(HttpStatusCode.OK);
        Assert.Contains(SpanishText.TooManyAttempts, throttled.Text, StringComparison.OrdinalIgnoreCase);
        var retryAfter = host.Api.Calls.Last().RetryAfter;
        Assert.NotNull(retryAfter);
        Assert.InRange(int.Parse(retryAfter!), 1, 3600);
        Assert.Contains(retryAfter, throttled.Text);
        WebFlows.AssertNoCookieIssued(second, throttled);
    }

    [Theory]
    [MemberData(nameof(BackendFailures))]
    public async Task Backend_failures_show_a_generic_unavailable_message_and_never_sign_in(string failure)
    {
        using var host = WebTestHost.Create(api);
        host.Api.Intercept = FakeApi.Failure(failure);
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, "someone@example.test", WrongPassword);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Contains(SpanishText.Unavailable, response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, host.Api.CallCount);
        WebFlows.AssertNoCookieIssued(session, response);
        WebFlows.AssertCleanFailurePage(response, WrongPassword);
        WebFlows.AssertNoStackTrace(response);
        Assert.Equal("someone@example.test", FormModel.From(response, response.FindPasswordForm()).Get("email"));
    }

    public static TheoryData<string> BackendFailures => FakeApi.BackendFailures;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("' OR 1=1 --")]
    [InlineData("a@b\nc@d")]
    [InlineData("ünï@例え.jp")]
    [InlineData("TOO-LONG")]
    public async Task Hostile_emails_re_render_the_form_with_a_visible_message_and_do_not_sign_in(string email)
    {
        if (email == "TOO-LONG")
        {
            email = new string('e', 5000) + "@example.test";
        }

        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var callsBefore = host.Api.CallCount;
        var response = await WebFlows.LoginAsync(session, email, WrongPassword);

        response.ShouldBe(HttpStatusCode.OK);
        WebFlows.AssertNoCookieIssued(session, response);
        WebFlows.AssertCleanFailurePage(response, WrongPassword);
        Assert.NotNull(response.FindPasswordForm());
        await WebFlows.AssertRejectionShownAsync(api, host, session, response, callsBefore, "/api/auth/login", new { email, password = WrongPassword }, email, WrongPassword);
        (await session.GetAsync("/users")).ShouldRedirectToLogin();
    }

    [Theory]
    [InlineData("")]
    [InlineData("HUGE")]
    public async Task Empty_and_enormous_passwords_re_render_the_form_with_a_visible_message_and_do_not_sign_in(string password)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        if (password == "HUGE")
        {
            password = new string('P', 50_000);
        }

        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var callsBefore = host.Api.CallCount;
        var response = await WebFlows.LoginAsync(session, owner.Email, password);

        response.ShouldBe(HttpStatusCode.OK);
        WebFlows.AssertNoCookieIssued(session, response);
        Assert.DoesNotContain(new string('P', 50), response.Body);
        WebFlows.AssertNoStackTrace(response);
        Assert.NotNull(response.FindPasswordForm());
        await WebFlows.AssertRejectionShownAsync(api, host, session, response, callsBefore, "/api/auth/login", new { email = owner.Email, password }, owner.Email);
    }

    private static void AssertHardenedCookie(string setCookie, string jwt)
    {
        var lower = setCookie.ToLowerInvariant();
        Assert.Contains("httponly", lower);
        Assert.Contains("secure", lower);
        Assert.True(lower.Contains("samesite=lax") || lower.Contains("samesite=strict"), $"SameSite must be Lax or Strict: {setCookie}");

        var value = setCookie.Split(';')[0][(setCookie.IndexOf('=') + 1)..];
        Assert.DoesNotContain(jwt, setCookie);
        Assert.False(PageResponse.ContainsJwt(value), "The cookie value must be opaque, not a JWT.");

        // The cookie may not outlive the API token it carries.
        var jwtExpiry = new DateTimeOffset(DateTime.SpecifyKind(ApiSeed.JwtExpiry(jwt), DateTimeKind.Utc));
        var slack = TimeSpan.FromSeconds(2);
        if (WebFlows.Attribute(setCookie, "expires") is { } expires)
        {
            Assert.True(DateTimeOffset.Parse(expires, System.Globalization.CultureInfo.InvariantCulture) <= jwtExpiry + slack, $"Cookie expires after the JWT: {setCookie}");
        }

        if (WebFlows.Attribute(setCookie, "max-age") is { } maxAge)
        {
            Assert.True(DateTimeOffset.UtcNow.AddSeconds(double.Parse(maxAge, System.Globalization.CultureInfo.InvariantCulture)) <= jwtExpiry + slack, $"Cookie max-age outlives the JWT: {setCookie}");
        }
    }
}
