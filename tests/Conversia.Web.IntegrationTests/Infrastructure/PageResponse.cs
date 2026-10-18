using System.Net;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>One buffered HTTP response from the Web under test, with an HTML view of its body.</summary>
internal sealed class PageResponse
{
    private static readonly Regex JwtShape = new(@"eyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+", RegexOptions.Compiled);

    private readonly Lazy<IHtmlDocument> _document;

    public PageResponse(Uri requestUri, HttpResponseMessage response, string body)
    {
        RequestUri = requestUri;
        StatusCode = response.StatusCode;
        Body = body;
        Headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        SetCookies = response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.ToList() : new List<string>();
        _document = new Lazy<IHtmlDocument>(() => new HtmlParser().ParseDocument(Body));
    }

    public Uri RequestUri { get; }

    public HttpStatusCode StatusCode { get; }

    public int Status => (int)StatusCode;

    public string Body { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public IReadOnlyList<string> SetCookies { get; }

    public IHtmlDocument Document => _document.Value;

    /// <summary>Visible text of the page, whitespace-collapsed, entity-decoded and Unicode-normalised (composed accents), so Spanish wording compares reliably.</summary>
    public string Text => Regex.Replace(Document.Body?.TextContent ?? string.Empty, @"\s+", " ").Trim().Normalize(System.Text.NormalizationForm.FormC);

    public bool IsRedirect => StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>The Location header as a site-relative path+query when it points at this site, else as given.</summary>
    public string? RedirectTarget
    {
        get
        {
            var location = Header("Location");
            if (location is null)
            {
                return null;
            }

            return Uri.TryCreate(location, UriKind.Absolute, out var absolute)
                && absolute.Host == WebTestHost.BaseAddress.Host
                ? absolute.PathAndQuery
                : location;
        }
    }

    public IEnumerable<IElement> Select(string selector) => Document.QuerySelectorAll(selector);

    /// <summary>The single form whose controls satisfy <paramref name="predicate"/>; fails the test (with the page text) when absent.</summary>
    public IHtmlFormElement FindForm(Func<IHtmlFormElement, bool> predicate, string description)
    {
        var forms = Document.Forms.Where(predicate).ToList();
        Assert.True(forms.Count == 1, $"Expected exactly one {description} on {RequestUri.PathAndQuery} (status {Status}); found {forms.Count}. Page text: {Truncate(Text)}");
        return forms[0];
    }

    /// <summary>The form with a password input — the login, registration or create-user form of the page.</summary>
    public IHtmlFormElement FindPasswordForm()
        => FindForm(f => f.QuerySelector("input[type=password]") is not null, "password form");

    public IHtmlFormElement FindLogoutForm()
        => FindForm(f => string.Equals(f.GetAttribute("method"), "post", StringComparison.OrdinalIgnoreCase)
                         && (f.GetAttribute("action") ?? string.Empty).TrimEnd('/').EndsWith("/logout", StringComparison.OrdinalIgnoreCase),
            "logout form (POST /logout)");

    /// <summary>The grant/revoke form for one user and permission (see <see cref="PageContract"/>).</summary>
    public IHtmlFormElement FindPermissionForm(string action, Guid userId, string permission)
        => FindForm(f => PageContract.IsPermissionForm(f, action, userId, permission), $"{action} form for user {userId} / {permission}");

    public IEnumerable<IHtmlFormElement> UsableForms()
        => Document.Forms.Where(PageContract.IsUsable);

    public string? AntiforgeryToken()
        => Document.QuerySelector("input[name='__RequestVerificationToken']")?.GetAttribute("value");

    /// <summary>Asserts the status code and returns this response for chaining.</summary>
    public PageResponse ShouldBe(HttpStatusCode expected)
    {
        Assert.True(expected == StatusCode, $"Expected {(int)expected} for {RequestUri.PathAndQuery} but got {Status}. Location={Header("Location")}. Page text: {Truncate(Text)}");
        return this;
    }

    public PageResponse ShouldRedirectTo(string path)
    {
        Assert.True(IsRedirect, $"Expected 302/303 for {RequestUri.PathAndQuery} but got {Status}. Page text: {Truncate(Text)}");
        Assert.Equal(path, RedirectTarget);
        return this;
    }

    /// <summary>Asserts a 302/303 whose target is the login page, returning its (decoded) returnUrl query value.</summary>
    public string? ShouldRedirectToLogin()
    {
        Assert.True(IsRedirect, $"Expected a redirect to /login for {RequestUri.PathAndQuery} but got {Status}. Page text: {Truncate(Text)}");
        var target = new Uri(WebTestHost.BaseAddress, RedirectTarget!);
        Assert.Equal("/login", target.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(target.Query);
        var key = query.AllKeys.FirstOrDefault(k => string.Equals(k, "returnUrl", StringComparison.OrdinalIgnoreCase));
        return key is null ? null : query[key];
    }

    public static bool ContainsJwt(string text) => JwtShape.IsMatch(text);

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "...";
}
