extern alias ApiHost;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>GET/POST /register: static SSR form over POST /api/auth/register-tenant.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class RegisterPageTests(TestApiFactory api)
{
    private const string WeakPassword = "Zq9xkWeak";

    [Fact]
    public async Task Get_register_renders_an_uncacheable_form_with_tenant_email_password_and_antiforgery_token()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = (await session.GetAsync("/register")).ShouldBe(HttpStatusCode.OK);

        var form = FormModel.From(page, page.FindPasswordForm());
        Assert.NotNull(form.Get("tenantName"));
        Assert.NotNull(form.Get("email"));
        Assert.NotNull(form.Get("password"));
        Assert.False(string.IsNullOrEmpty(form.Get(FormModel.AntiforgeryField)));
        WebFlows.AssertNotCacheable(page);
        Assert.Equal(0, host.Api.CallCount);
    }

    [Fact]
    public async Task Successful_registration_creates_the_tenant_signs_the_owner_in_and_redirects_home()
    {
        var tenantName = ApiSeed.UniqueTenantName("web-register");
        var email = ApiSeed.UniqueEmail("web-owner");
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.RegisterAsync(session, tenantName, email, ApiSeed.StrongPassword);

        response.ShouldRedirectTo("/panel");
        WebFlows.AssertNotCacheable(response);
        Assert.True(session.HasAuthCookie);

        // A profile fetch after sign-in would be fine; the registration POST itself must happen exactly once.
        var call = Assert.Single(host.Api.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery == "/api/auth/register-tenant");
        Assert.Equal("http://api.test/api/auth/register-tenant", call.Uri.GetLeftPart(UriPartial.Path));
        Assert.Equal(HttpStatusCode.Created, call.Status);
        using (var sent = JsonDocument.Parse(call.Body!))
        {
            Assert.Equal(tenantName, sent.RootElement.GetProperty("tenantName").GetString());
            Assert.Equal(email, sent.RootElement.GetProperty("email").GetString());
        }

        var cookies = WebFlows.IssuedCookies(response);
        var token = Assert.Single(host.Api.IssuedTokens);
        Assert.NotEmpty(cookies); // a large ticket may be chunked into several cookies
        Assert.All(cookies, cookie =>
        {
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase); // beyond the requirement; valid because the test host is https
            Assert.DoesNotContain(token, cookie);
        });
        Assert.Equal(1, await ApiSeed.CountTenantsNamedAsync(api, tenantName));

        var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        var tenantId = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.First(c => c.Type == "tenant_id").Value;
        Assert.Equal(tenantId, home.Document.QuerySelector(PageContract.ProfileTenantId)?.TextContent.Trim());
        Assert.Equal("true", home.Document.QuerySelector(PageContract.ProfileIsOwner)?.GetAttribute("data-value"));

        WebFlows.AssertNoJwtLeaked(session, host);
        WebFlows.AssertNoSecretLogged(host, ApiSeed.StrongPassword);
    }

    [Fact]
    public async Task Registering_an_email_that_exists_shows_the_conflict_message_without_signing_in()
    {
        var existing = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.RegisterAsync(session, ApiSeed.UniqueTenantName("dup"), existing.Email, ApiSeed.StrongPassword);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Contains(SpanishText.EmailTaken, response.Text);
        WebFlows.AssertNoCookieIssued(session, response);
        WebFlows.AssertCleanFailurePage(response, ApiSeed.StrongPassword);
        Assert.Equal(existing.Email, FormModel.From(response, response.FindPasswordForm()).Get("email"));
        Assert.Equal(1, await ApiSeed.CountUsersWithEmailAsync(api, existing.Email));
        var attempt = Assert.Single(host.Api.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery == "/api/auth/register-tenant");
        Assert.Equal(HttpStatusCode.Conflict, attempt.Status);
    }

    [Fact]
    public async Task Weak_password_shows_the_apis_own_policy_message_and_never_echoes_the_password()
    {
        var tenantName = ApiSeed.UniqueTenantName("weak");
        var email = ApiSeed.UniqueEmail("weak");
        var (status, reference) = await ApiSeed.PostForValidationAsync(api, "/api/auth/register-tenant",
            new { tenantName, email, password = WeakPassword });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        var policyMessage = Assert.Single(reference["Password"]);

        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var response = await WebFlows.RegisterAsync(session, tenantName, email, WeakPassword);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Contains(policyMessage, response.Text);
        WebFlows.AssertNoCookieIssued(session, response);
        WebFlows.AssertCleanFailurePage(response, WeakPassword);
        var form = FormModel.From(response, response.FindPasswordForm());
        Assert.Equal(tenantName, form.Get("tenantName"));
        Assert.Equal(email, form.Get("email"));
        Assert.Equal(0, await ApiSeed.CountTenantsNamedAsync(api, tenantName));
        WebFlows.AssertNoSecretLogged(host, WeakPassword);
    }

    [Fact]
    public async Task Api_validation_errors_for_every_field_are_shown()
    {
        using var host = WebTestHost.Create(api);
        host.Api.Intercept = _ => FakeApi.Validation(
            ("TenantName", "Stub says: tenant name is bad."),
            ("Email", "Stub says: email is bad."),
            ("Password", "Stub says: password is bad."));
        using var session = host.NewSession();

        var response = await WebFlows.RegisterAsync(session, "Acme", "new@example.test", ApiSeed.StrongPassword);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Contains("Stub says: tenant name is bad.", response.Text);
        Assert.Contains("Stub says: email is bad.", response.Text);
        Assert.Contains("Stub says: password is bad.", response.Text);
        Assert.Equal(1, host.Api.CallCount);
        WebFlows.AssertNoCookieIssued(session, response);
        WebFlows.AssertCleanFailurePage(response, ApiSeed.StrongPassword);
    }

    [Theory]
    [InlineData("", "VALID")]
    [InlineData("   ", "VALID")]
    [InlineData("Acme", "not-an-email")]
    [InlineData("Acme", "")]
    [InlineData("TOO-LONG-NAME", "VALID")]
    public async Task Invalid_registration_input_re_renders_the_form_with_a_visible_message_without_creating_anything(string tenantName, string email)
    {
        if (tenantName == "TOO-LONG-NAME")
        {
            tenantName = new string('t', 201);
        }

        if (email == "VALID")
        {
            email = ApiSeed.UniqueEmail("invalid");
        }

        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var callsBefore = host.Api.CallCount;
        var response = await WebFlows.RegisterAsync(session, tenantName, email, ApiSeed.StrongPassword);

        response.ShouldBe(HttpStatusCode.OK);
        WebFlows.AssertNoCookieIssued(session, response);
        WebFlows.AssertCleanFailurePage(response, ApiSeed.StrongPassword);
        Assert.NotNull(response.FindPasswordForm());
        await WebFlows.AssertRejectionShownAsync(api, host, session, response, callsBefore, "/api/auth/register-tenant",
            new { tenantName, email, password = ApiSeed.StrongPassword }, tenantName, email, ApiSeed.StrongPassword);
        Assert.Equal(0, await ApiSeed.CountUsersWithEmailAsync(api, email));
        Assert.Equal(0, await ApiSeed.CountTenantsNamedAsync(api, tenantName));
    }

    [Fact]
    public async Task Api_rate_limit_is_reported_with_the_retry_after_seconds()
    {
        using var host = WebTestHost.Create(api);
        host.Api.Intercept = _ => FakeApi.TooManyRequests(11);
        using var session = host.NewSession();

        var response = await WebFlows.RegisterAsync(session, "Acme", "new@example.test", ApiSeed.StrongPassword);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Contains(SpanishText.TooManyAttempts, response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("11", response.Text);
        WebFlows.AssertNoCookieIssued(session, response);
        WebFlows.AssertCleanFailurePage(response, ApiSeed.StrongPassword);
    }

    [Theory]
    [MemberData(nameof(BackendFailures))]
    public async Task Backend_failures_show_a_generic_unavailable_message_and_never_sign_in(string failure)
    {
        using var host = WebTestHost.Create(api);
        host.Api.Intercept = FakeApi.Failure(failure);
        using var session = host.NewSession();

        var response = await WebFlows.RegisterAsync(session, "Acme", "new@example.test", ApiSeed.StrongPassword);

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Contains(SpanishText.Unavailable, response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, host.Api.CallCount);
        WebFlows.AssertNoCookieIssued(session, response);
        WebFlows.AssertCleanFailurePage(response, ApiSeed.StrongPassword);
        var form = FormModel.From(response, response.FindPasswordForm());
        Assert.Equal("Acme", form.Get("tenantName"));
        Assert.Equal("new@example.test", form.Get("email"));
    }

    public static TheoryData<string> BackendFailures => FakeApi.BackendFailures;

    [Fact]
    public async Task Racing_registrations_of_one_email_through_the_web_sign_in_exactly_one_owner()
    {
        const int racers = 6;
        var email = ApiSeed.UniqueEmail("race");
        using var host = WebTestHost.Create(api);
        var sessions = Enumerable.Range(0, racers).Select(_ => host.NewSession()).ToList();

        var responses = await Task.WhenAll(sessions.Select(session =>
            WebFlows.RegisterAsync(session, ApiSeed.UniqueTenantName("race"), email, ApiSeed.StrongPassword)));

        var winners = responses.Where(r => r.IsRedirect).ToList();
        var losers = responses.Where(r => !r.IsRedirect).ToList();
        Assert.Single(winners);
        Assert.Equal(racers - 1, losers.Count);
        Assert.All(losers, loser =>
        {
            Assert.Equal(HttpStatusCode.OK, loser.StatusCode);
            Assert.Contains(SpanishText.EmailTaken, loser.Text);
            Assert.Empty(WebFlows.IssuedCookies(loser));
        });
        Assert.Equal(1, sessions.Count(s => s.HasAuthCookie));
        Assert.Equal(1, await ApiSeed.CountUsersWithEmailAsync(api, email));
        sessions.ForEach(s => s.Dispose());
    }
}
