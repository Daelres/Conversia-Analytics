using System.Globalization;
using System.Net;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>Browser-level flows (fetch the form, fill it, submit it) and the assertions every suite shares.</summary>
internal static class WebFlows
{
    public static async Task<PageResponse> LoginAsync(WebSession session, string email, string password, string? returnUrl = null)
    {
        var url = returnUrl is null ? "/login" : "/login?returnUrl=" + Uri.EscapeDataString(returnUrl);
        var page = (await session.GetAsync(url)).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(page, page.FindPasswordForm()).Set("email", email).Set("password", password);
        return await session.SubmitAsync(form);
    }

    public static async Task<PageResponse> RegisterAsync(WebSession session, string tenantName, string email, string password)
    {
        var page = (await session.GetAsync("/register")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(page, page.FindPasswordForm())
            .Set("tenantName", tenantName).Set("email", email).Set("password", password);
        return await session.SubmitAsync(form);
    }

    /// <summary>A session that has logged in through the Web (asserting the redirect and the cookie).</summary>
    public static async Task<WebSession> SignedInAsync(WebTestHost host, string email, string password = ApiSeed.StrongPassword)
    {
        var session = host.NewSession();
        var response = await LoginAsync(session, email, password);
        Assert.True(response.IsRedirect, $"Login should redirect, got {response.Status}. Page text: {response.Text}");
        Assert.True(session.HasAuthCookie, "Login should leave an authentication cookie in the jar.");
        return session;
    }

    /// <summary>The administrator's sign-in form (<c>/admin/login</c>), filled and submitted.</summary>
    public static async Task<PageResponse> AdminLoginAsync(WebSession session, string email, string password, string? returnUrl = null)
    {
        var url = returnUrl is null ? "/admin/login" : "/admin/login?returnUrl=" + Uri.EscapeDataString(returnUrl);
        var page = (await session.GetAsync(url)).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(page, page.FindPasswordForm()).Set("email", email).Set("password", password);
        return await session.SubmitAsync(form);
    }

    /// <summary>A session that has signed in as a platform administrator through the Web (asserting the redirect and the cookie).</summary>
    public static async Task<WebSession> SignedInAdminAsync(WebTestHost host, ApiSeed.Admin admin)
    {
        var session = host.NewSession();
        var response = await AdminLoginAsync(session, admin.Email, admin.Password);
        Assert.True(response.IsRedirect, $"Admin login should redirect, got {response.Status}. Page text: {response.Text}");
        Assert.True(session.HasAuthCookie, "Admin login should leave an authentication cookie in the jar.");
        return session;
    }

    /// <summary>A valid antiforgery token for this session, taken from its logout form.</summary>
    public static async Task<string> AntiforgeryTokenAsync(WebSession session)
    {
        var page = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(page, page.FindLogoutForm());
        return form.Get(FormModel.AntiforgeryField) ?? throw new Xunit.Sdk.XunitException("Logout form carries no antiforgery token.");
    }

    /// <summary>Post/Redirect/Get is as good as an inline re-render: land on the page the user ends up seeing.</summary>
    public static async Task<PageResponse> FollowRedirectsAsync(WebSession session, PageResponse response)
    {
        for (var hops = 0; response.IsRedirect && hops < 3; hops++)
        {
            response = await session.FollowAsync(response);
        }

        return response;
    }

    public static async Task<PageResponse> UsersAsync(WebSession session)
        => (await session.GetAsync("/users")).ShouldBe(HttpStatusCode.OK);

    public static IReadOnlyList<Guid> ListedUserIds(PageResponse usersPage)
        => usersPage.Select(PageContract.UserRow).Select(r => Guid.Parse(r.GetAttribute("data-user-id")!)).ToList();

    /// <summary>
    /// The auth cookies a response sets: not the antiforgery cookie, not a deletion. More than one entry is legitimate
    /// (a ticket may be chunked), so callers assert on every entry rather than on a single one.
    /// </summary>
    public static IReadOnlyList<string> IssuedCookies(PageResponse response)
        => response.SetCookies.Where(c => !IsAntiforgery(c) && !IsDeletion(c)).ToList();

    public static bool ClearsAuthCookie(PageResponse response)
        => response.SetCookies.Any(c => !IsAntiforgery(c) && IsDeletion(c));

    public static void AssertNoCookieIssued(WebSession session, PageResponse response)
    {
        Assert.Empty(IssuedCookies(response));
        Assert.False(session.HasAuthCookie, "No authentication cookie may be in the jar.");
    }

    public static void AssertCleanFailurePage(PageResponse response, string secretToAbsent)
    {
        Assert.DoesNotContain(secretToAbsent, response.Body);
        AssertNoStackTrace(response);
        var password = response.Document.QuerySelector("input[type=password]");
        if (password is not null)
        {
            Assert.True(string.IsNullOrEmpty(password.GetAttribute("value")), "The password input must be re-rendered empty.");
        }
    }

    public static void AssertNoStackTrace(PageResponse response)
    {
        Assert.DoesNotContain("   at ", response.Body);
        Assert.DoesNotContain("Exception", response.Body);
        Assert.DoesNotContain("StackTrace", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".cs:line", response.Body);
    }

    /// <summary>No response of the conversation shows a JWT in its body or headers, and no cookie value is (or contains) one.</summary>
    public static void AssertNoJwtLeaked(WebSession session, WebTestHost host)
    {
        var issued = host.Api.IssuedTokens;
        foreach (var response in session.Transcript)
        {
            var haystacks = response.Headers.Values.Concat(response.SetCookies).Append(response.Body);
            foreach (var text in haystacks)
            {
                Assert.False(PageResponse.ContainsJwt(text), $"A JWT appears in the response to {response.RequestUri.PathAndQuery}.");
                foreach (var token in issued)
                {
                    Assert.DoesNotContain(token, text);
                }
            }
        }
    }

    public static void AssertNoSecretLogged(WebTestHost host, params string[] secrets)
    {
        foreach (var entry in host.Logs.Entries)
        {
            Assert.False(PageResponse.ContainsJwt(entry), "A JWT was logged.");
            foreach (var secret in secrets.Concat(host.Api.IssuedTokens))
            {
                Assert.DoesNotContain(secret, entry);
            }
        }
    }

    /// <summary>
    /// A rejected form must show a visible reason, not silently re-render. When the Web forwarded the submission to the API
    /// (any API call since <paramref name="callsBefore"/>), the reason is what the API itself answers for the same request body:
    /// each validation message it would return (at least one must be on the page), or the generic "Correo o contraseña incorrectos."
    /// when the API answers 401 (login only). When the Web rejected the input locally and made no API call (client-side
    /// DataAnnotations are legitimate), any visible message will do: the page text, minus the echoed form values, must differ
    /// from the pristine form page.
    /// </summary>
    public static async Task AssertRejectionShownAsync(
        TestApiFactory api, WebTestHost host, WebSession session, PageResponse response, int callsBefore, string apiPath, object body, params string[] echoedValues)
    {
        if (host.Api.CallCount == callsBefore)
        {
            var pristine = (await session.GetAsync(response.RequestUri.PathAndQuery)).ShouldBe(HttpStatusCode.OK);
            Assert.NotEqual(WithoutEchoes(pristine.Text, echoedValues), WithoutEchoes(response.Text, echoedValues));
            return;
        }

        var (status, errors) = await ApiSeed.PostForValidationAsync(api, apiPath, body);
        if (status == HttpStatusCode.Unauthorized)
        {
            Assert.Contains(SpanishText.InvalidCredentials, response.Text);
            return;
        }

        Assert.True(status == HttpStatusCode.BadRequest, $"The test expected the API to refuse {apiPath} with 400 or 401 but it answered {(int)status}.");
        var messages = errors.Values.SelectMany(m => m).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();
        Assert.NotEmpty(messages);
        Assert.True(
            messages.Any(m => response.Text.Contains(m, StringComparison.Ordinal)),
            $"The page shows none of the API's validation messages ({string.Join(" | ", messages)}). Page text: {response.Text}");
    }

    private static string WithoutEchoes(string text, string[] echoedValues)
        => echoedValues.Where(v => !string.IsNullOrWhiteSpace(v)).Aggregate(text, (t, v) => t.Replace(v.Trim(), string.Empty, StringComparison.Ordinal));

    /// <summary>Auth pages and their form posts must not be stored by browsers or proxies.</summary>
    public static void AssertNotCacheable(PageResponse response)
    {
        var cacheControl = response.Header("Cache-Control") ?? string.Empty;
        Assert.DoesNotContain("public", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase) || cacheControl.Contains("no-cache", StringComparison.OrdinalIgnoreCase),
            $"{response.RequestUri.PathAndQuery} ({response.Status}) must send Cache-Control no-store or no-cache; got '{cacheControl}'.");
    }

    /// <summary>Pages carrying tenant data must not be stored at all (no-cache alone still lets a cache keep a copy).</summary>
    public static void AssertNotStored(PageResponse response)
    {
        var cacheControl = response.Header("Cache-Control") ?? string.Empty;
        Assert.DoesNotContain("public", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase),
            $"{response.RequestUri.PathAndQuery} ({response.Status}) carries tenant data and must send Cache-Control no-store; got '{cacheControl}'.");
    }

    public static bool IsAntiforgery(string setCookie)
        => setCookie.Contains("antiforgery", StringComparison.OrdinalIgnoreCase);

    public static bool IsDeletion(string setCookie)
    {
        var parts = setCookie.Split(';', StringSplitOptions.TrimEntries);
        var value = parts[0][(parts[0].IndexOf('=') + 1)..];
        if (value.Length == 0)
        {
            return true;
        }

        var expires = Attribute(setCookie, "expires");
        return expires is not null
            && DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            && when < DateTimeOffset.UtcNow;
    }

    public static string? Attribute(string setCookie, string name)
    {
        foreach (var part in setCookie.Split(';', StringSplitOptions.TrimEntries).Skip(1))
        {
            var pair = part.Split('=', 2);
            if (string.Equals(pair[0], name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Length == 2 ? pair[1] : string.Empty;
            }
        }

        return null;
    }
}
