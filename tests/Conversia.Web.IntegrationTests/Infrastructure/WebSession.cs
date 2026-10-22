using System.Net;
using System.Text;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// A browser tab: keeps cookies, never follows redirects on its own, and records every
/// response so a test can scan the whole conversation (e.g. for a leaked JWT).
/// </summary>
internal sealed class WebSession : IDisposable
{
    private readonly HttpClient _client;
    private readonly CookieContainer _cookies;
    private readonly List<PageResponse> _transcript = new();

    public WebSession(HttpClient client, CookieContainer cookies)
    {
        _client = client;
        _cookies = cookies;
    }

    public IReadOnlyList<PageResponse> Transcript
    {
        get
        {
            lock (_transcript)
            {
                return _transcript.ToArray();
            }
        }
    }

    /// <summary>
    /// Cookies in the jar other than the antiforgery cookie. Assumption (see <see cref="PageContract"/>): the Web sets no
    /// cookie besides the antiforgery cookie and the authentication ticket (possibly chunked), so these are the auth cookie(s).
    /// </summary>
    public IReadOnlyList<Cookie> AuthCookies
        => _cookies.GetCookies(WebTestHost.BaseAddress).Cast<Cookie>()
            .Where(c => !c.Name.Contains("antiforgery", StringComparison.OrdinalIgnoreCase) && c.Value.Length > 0)
            .ToList();

    public bool HasAuthCookie => AuthCookies.Count > 0;

    /// <summary>The same browser talking to another server process: carries over every cookie <paramref name="other"/> holds.</summary>
    public void CopyCookiesFrom(WebSession other)
    {
        foreach (Cookie cookie in other._cookies.GetCookies(WebTestHost.BaseAddress))
        {
            _cookies.Add(WebTestHost.BaseAddress, new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
        }
    }

    /// <summary>The value of a cookie of the jar, by name; null when there is none.</summary>
    public string? CookieValue(string name)
        => _cookies.GetCookies(WebTestHost.BaseAddress).Cast<Cookie>().FirstOrDefault(c => c.Name == name)?.Value;

    /// <summary>Puts a cookie in the jar as a browser would hold it (the forged-cookie case: one kind of session's value under the other's name).</summary>
    public void SetCookie(string name, string value)
        => _cookies.Add(WebTestHost.BaseAddress, new Cookie(name, value, "/", WebTestHost.BaseAddress.Host));

    /// <summary>Removes a cookie from the jar.</summary>
    public void RemoveCookie(string name)
        => _cookies.Add(WebTestHost.BaseAddress, new Cookie(name, string.Empty, "/", WebTestHost.BaseAddress.Host) { Expires = DateTime.UtcNow.AddDays(-1) });

    public async Task<PageResponse> GetAsync(string url, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await SendAsync(request);
    }

    /// <summary>A page read the caller can walk away from: cancelling <paramref name="cancellationToken"/> aborts the request on the server too.</summary>
    public async Task<PageResponse> GetAbortableAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new PageResponse(new Uri(WebTestHost.BaseAddress, url), response, body);
    }

    /// <summary>A static file: the buffered response plus the exact bytes served (not recorded in the transcript).</summary>
    public async Task<(PageResponse Response, byte[] Bytes)> GetFileAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return (new PageResponse(new Uri(WebTestHost.BaseAddress, url), response, Encoding.UTF8.GetString(bytes)), bytes);
    }

    public async Task<PageResponse> SubmitAsync(FormModel form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, form.ActionUri)
        {
            Content = new FormUrlEncodedContent(form.Fields)
        };
        return await SendAsync(request);
    }

    public async Task<PageResponse> PostRawAsync(string url, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        return await SendAsync(request);
    }

    /// <summary>Follows one redirect the way a browser would (GET on the Location target).</summary>
    public Task<PageResponse> FollowAsync(PageResponse redirect)
    {
        Assert.True(redirect.IsRedirect, $"Cannot follow a {redirect.Status} response.");
        return GetAsync(redirect.RedirectTarget!);
    }

    private async Task<PageResponse> SendAsync(HttpRequestMessage request)
    {
        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var buffered = new PageResponse(request.RequestUri is { IsAbsoluteUri: true } uri ? uri : new Uri(WebTestHost.BaseAddress, request.RequestUri!), response, body);
        lock (_transcript)
        {
            _transcript.Add(buffered);
        }

        return buffered;
    }

    public void Dispose() => _client.Dispose();
}
