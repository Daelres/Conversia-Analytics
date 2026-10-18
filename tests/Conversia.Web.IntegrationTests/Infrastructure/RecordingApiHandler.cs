using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>One request the Web sent to the API, as the API side saw it.</summary>
internal sealed record ApiCall(
    HttpMethod Method,
    Uri Uri,
    string? Authorization,
    string? Body,
    HttpStatusCode? Status,
    string? RetryAfter)
{
    public string PathAndQuery => Uri.PathAndQuery;

    public string? BearerToken => Authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true
        ? Authorization["Bearer ".Length..]
        : null;
}

/// <summary>
/// The primary handler of every Web-to-API HttpClient in a test host: records each request and,
/// unless <see cref="Intercept"/> answers it (or throws), forwards it to the in-process API.
/// </summary>
internal sealed class RecordingApiHandler : DelegatingHandler
{
    private readonly ConcurrentQueue<ApiCall> _calls = new();
    private readonly ConcurrentQueue<string> _issuedTokens = new();

    public RecordingApiHandler(HttpMessageHandler apiHandler) : base(apiHandler)
    {
    }

    /// <summary>
    /// Replaces the API's answer: return a response to fake it, return null to reach the real API,
    /// or throw to simulate a transport failure. May be changed while a test runs.
    /// </summary>
    public Func<HttpRequestMessage, Task<HttpResponseMessage?>>? Intercept { get; set; }

    public IReadOnlyList<ApiCall> Calls => _calls.ToArray();

    public int CallCount => _calls.Count;

    /// <summary>Every JWT the real API issued through this handler (login / register-tenant).</summary>
    public IReadOnlyList<string> IssuedTokens => _issuedTokens.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken);
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var authorization = request.Headers.Authorization?.ToString();
        var intercept = Intercept;

        HttpResponseMessage response;
        try
        {
            response = (intercept is null ? null : await intercept(request)) ?? await base.SendAsync(request, cancellationToken);
        }
        catch
        {
            _calls.Enqueue(new ApiCall(request.Method, request.RequestUri!, authorization, body, null, null));
            throw;
        }

        string? retryAfter = response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
        _calls.Enqueue(new ApiCall(request.Method, request.RequestUri!, authorization, body, response.StatusCode, retryAfter));
        await RememberIssuedTokenAsync(request, response, cancellationToken);
        return response;
    }

    private async Task RememberIssuedTokenAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var isAuthCall = path.EndsWith("/api/auth/login", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/api/auth/register-tenant", StringComparison.OrdinalIgnoreCase);
        if (!isAuthCall || !response.IsSuccessStatusCode || response.Content is null)
        {
            return;
        }

        await response.Content.LoadIntoBufferAsync(cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("token", out var token)
                && token.ValueKind == JsonValueKind.String
                && token.GetString() is { Length: > 0 } value)
            {
                _issuedTokens.Enqueue(value);
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>The handler is shared by the host's HttpClientFactory, which must not dispose the API handler under it.</summary>
    protected override void Dispose(bool disposing)
    {
    }
}

/// <summary>Canned API answers for tests that simulate a misbehaving backend.</summary>
internal static class FakeApi
{
    public static Task<HttpResponseMessage?> Respond(HttpStatusCode status, string? body = null, string contentType = "application/problem+json", string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return Task.FromResult<HttpResponseMessage?>(response);
    }

    public static Task<HttpResponseMessage?> Validation(params (string Field, string Message)[] errors)
    {
        var dictionary = errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());
        var body = JsonSerializer.Serialize(new { title = "One or more validation errors occurred.", status = 400, errors = dictionary });
        return Respond(HttpStatusCode.BadRequest, body);
    }

    public static Task<HttpResponseMessage?> TooManyRequests(int retryAfterSeconds)
        => Respond(HttpStatusCode.TooManyRequests, """{"title":"Too Many Requests","status":429}""", retryAfter: retryAfterSeconds.ToString());

    public static readonly IReadOnlyList<string> BackendFailureNames = new[]
    {
        "connection refused",
        "timeout",
        "500 problem",
        "503 empty",
        "502 html",
        "200 html",
        "200 empty",
        "200 truncated json",
        "400 html",
    };

    public static TheoryData<string> BackendFailures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in BackendFailureNames)
            {
                data.Add(name);
            }

            return data;
        }
    }

    /// <summary>One failure of each distinct kind (transport, server error, wrong content) for pages that only need to prove they map failures at all.</summary>
    public static TheoryData<string> RepresentativeBackendFailures => new() { "connection refused", "500 problem", "200 html" };

    /// <summary>An <see cref="RecordingApiHandler.Intercept"/> that makes every API call fail in the named way.</summary>
    public static Func<HttpRequestMessage, Task<HttpResponseMessage?>> Failure(string name) => name switch
    {
        "connection refused" => _ => throw new HttpRequestException("connection refused (simulated)"),
        "timeout" => _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout (simulated).", new TimeoutException()),
        "500 problem" => _ => Respond(HttpStatusCode.InternalServerError, """{"title":"An unexpected error occurred.","status":500}"""),
        "503 empty" => _ => Respond(HttpStatusCode.ServiceUnavailable),
        "502 html" => _ => Respond(HttpStatusCode.BadGateway, "<html><body>Bad gateway</body></html>", "text/html"),
        "200 html" => _ => Respond(HttpStatusCode.OK, "<html><body>not the API</body></html>", "text/html"),
        "200 empty" => _ => Respond(HttpStatusCode.OK),
        "200 truncated json" => _ => Respond(HttpStatusCode.OK, """{"token":"abc","userI""", "application/json"),
        "400 html" => _ => Respond(HttpStatusCode.BadRequest, "<html><body>bad</body></html>", "text/html"),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    public static bool IsPath(HttpRequestMessage request, string path)
        => string.Equals(request.RequestUri!.AbsolutePath, path, StringComparison.OrdinalIgnoreCase);
}
