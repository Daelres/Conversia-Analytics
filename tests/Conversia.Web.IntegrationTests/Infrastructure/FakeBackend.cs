using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Diagnostics;
using Conversia.Application.DTOs.Products;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Common;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>One request the Web sent to the fake backend: every header (content headers included), the body and the caller's cancellation.</summary>
internal sealed record BackendRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body, CancellationToken Cancellation)
{
    public string Path => Uri.AbsolutePath;

    public bool IsReport => FakeBackend.IsReportPath(Path);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>The body as the real API would bind it.</summary>
    public ReportErrorRequest ReportBody => JsonSerializer.Deserialize<ReportErrorRequest>(Body!, FakeBackend.Json)!;

    public JsonDocument BodyJson() => JsonDocument.Parse(Body!);
}

/// <summary>
/// A stand-in for Conversia.API, as seen by the Web: a healthy API (login, profile, users, products) whose answers a test can
/// override, plus the error report endpoint with a behaviour of its own. It keeps no database and no state besides the record of
/// what it received, so tests that only need "an API that answers like this" start instantly and in parallel.
/// Everything it receives is recorded, including the headers: a test can prove what the Web never sends (cookies, bearer tokens).
/// </summary>
internal sealed class FakeBackend : HttpMessageHandler
{
    public const string ReportPath = "/api/diagnostics/errors";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The handler is shared by the host's HttpClientFactory, which must not dispose it under the test.</summary>
    protected override void Dispose(bool disposing)
    {
    }

    public static readonly Guid UserId = Guid.Parse("5b0d0b5e-39a5-4d88-8a2f-3d9b2f0d7e11");

    public static readonly Guid TenantId = Guid.Parse("a1f0c2d4-0000-4000-8000-000000000001");

    private readonly ConcurrentQueue<BackendRequest> _requests = new();

    /// <summary>Answers every request that is not a report. Return null to fall through to the healthy default.</summary>
    public Func<BackendRequest, Task<HttpResponseMessage?>>? Data { get; set; }

    /// <summary>Answers <c>POST /api/diagnostics/errors</c>; by default it accepts the report and echoes the code (the API honours a valid unused one).</summary>
    public Func<BackendRequest, Task<HttpResponseMessage>> Reports { get; set; } = AcceptReport;

    public IReadOnlyList<BackendRequest> Requests => _requests.ToArray();

    public IReadOnlyList<BackendRequest> ReportRequests => _requests.Where(r => r.IsReport).ToArray();

    public IReadOnlyList<BackendRequest> DataRequests => _requests.Where(r => !r.IsReport).ToArray();

    public static bool IsReportPath(string path) => string.Equals(path.TrimEnd('/'), ReportPath, StringComparison.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new BackendRequest(request.Method, request.RequestUri!, headers, body, cancellationToken);
        _requests.Enqueue(recorded);

        if (recorded.IsReport)
        {
            return await Reports(recorded);
        }

        var custom = Data is null ? null : await Data(recorded);
        return custom ?? Healthy(recorded);
    }

    // ---- the healthy API ----

    private static HttpResponseMessage Healthy(BackendRequest request)
    {
        var path = request.Path.TrimEnd('/');
        return (request.Method.Method, path) switch
        {
            ("POST", "/api/auth/login") => Answer(HttpStatusCode.OK, Auth(EmailOf(request))),
            ("POST", "/api/auth/register-tenant") => Answer(HttpStatusCode.Created, Auth(EmailOf(request))),
            ("GET", "/api/users/me") => Answer(HttpStatusCode.OK, Profile(request)),
            ("GET", "/api/users") => Answer(HttpStatusCode.OK, new[] { new UserSummaryResponse { UserId = UserId, Email = "owner@example.test", IsOwner = true } }),
            ("POST", "/api/users") => Answer(HttpStatusCode.Created, new UserSummaryResponse { UserId = Guid.NewGuid(), Email = "new@example.test" }),
            ("GET", "/api/products") => Answer(HttpStatusCode.OK, new PagedResponse<ProductResponse> { Items = [], Page = 1, PageSize = 10, TotalCount = 0 }),
            ("POST", "/api/products") => Answer(HttpStatusCode.Created, Product(Guid.NewGuid())),
            ("GET", _) when path.StartsWith("/api/products/", StringComparison.Ordinal) => Answer(HttpStatusCode.OK, Product(Guid.Parse(path["/api/products/".Length..]))),
            _ => Answer(HttpStatusCode.NoContent, null),
        };
    }

    /// <summary>
    /// The identity a login answers with: every e-mail address is a person of its own (and of a workspace of its own), so that tests
    /// with several people can tell whose data a report carries. <c>null</c> is the default person (<see cref="UserId"/>).
    /// </summary>
    public static AuthResponse Auth(string? email = null)
    {
        var (user, tenant) = IdentityOf(email);
        return new AuthResponse { Token = FakeJwt.Create(user, tenant), UserId = user, TenantId = tenant, IsOwner = true };
    }

    public static (Guid UserId, Guid TenantId) IdentityOf(string? email)
        => email is null
            ? (UserId, TenantId)
            : (Hash("user:" + email.ToLowerInvariant()), Hash("tenant:" + email.ToLowerInvariant()));

    private static Guid Hash(string text) => new(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(text)));

    private static string? EmailOf(BackendRequest request)
    {
        try
        {
            using var body = request.BodyJson();
            return body.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UserProfileResponse Profile(BackendRequest request)
    {
        var (user, tenant) = FakeJwt.IdentityOf(request.Header("Authorization")?["Bearer ".Length..]);
        return new UserProfileResponse { UserId = user, TenantId = tenant, IsOwner = true };
    }

    private static ProductResponse Product(Guid id) => new()
    {
        Id = id, Name = "Yunque", Sku = "YUN-1", Price = 10.5m, Stock = 3, RowVersion = "AAAAAAAAB9E=", CreatedAt = DateTime.UtcNow,
    };

    private static Task<HttpResponseMessage> AcceptReport(BackendRequest request)
    {
        var code = request.ReportBody.Code is { Length: > 0 } sent ? sent : ErrorCode.New(DateTime.UtcNow);
        return Task.FromResult(Answer(HttpStatusCode.Created, new ReportErrorResponse(code, true)));
    }

    // ---- answers a test can plug in ----

    public static HttpResponseMessage Answer(HttpStatusCode status, object? body, string contentType = "application/json")
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, contentType);
        }

        return response;
    }

    public static HttpResponseMessage Raw(HttpStatusCode status, string? body, string contentType = "application/problem+json", string? retryAfter = null, string? errorIdHeader = null)
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

        if (errorIdHeader is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Error-Id", errorIdHeader);
        }

        return response;
    }

    /// <summary>What the real API answers to an unexpected failure: 500 problem+json with <c>errorId</c> and the <c>X-Error-Id</c> header.</summary>
    public static HttpResponseMessage UnexpectedWithCode(string code)
        => Raw(HttpStatusCode.InternalServerError,
            $$"""{"title":"Ocurrió un error inesperado","status":500,"detail":"Ocurrió un error inesperado. Inténtalo de nuevo más tarde.","errorId":"{{code}}"}""",
            errorIdHeader: code);

    /// <summary>Hangs until the caller gives up (the HttpClient timeout), then fails the way a timed-out request does.</summary>
    public static async Task<HttpResponseMessage> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>The API is down: the connection is refused.</summary>
    public static Task<HttpResponseMessage> Refused(BackendRequest _)
        => throw new HttpRequestException("connection refused (simulated)");

    /// <summary>The failures of the report endpoint the Web must survive, by name (a Theory axis).</summary>
    public static readonly IReadOnlyList<string> ReportFailureNames =
    [
        "connection refused", "timeout", "401", "404 (no key configured)", "429", "500", "413", "400",
        "201 html", "201 empty", "201 truncated json", "201 json without a code", "201 json with an invalid code",
    ];

    /// <summary>The API did receive and accept the report (a 201) but its answer cannot be used; every other failure is a report that did not get through.</summary>
    public static bool ReachedTheApi(string failureName) => failureName.StartsWith("201", StringComparison.Ordinal);

    public static Func<BackendRequest, Task<HttpResponseMessage>> ReportFailure(string name) => name switch
    {
        "connection refused" => Refused,
        "timeout" => request => HangAsync(request.Cancellation),
        "401" => _ => Task.FromResult(Raw(HttpStatusCode.Unauthorized, null)),
        "404 (no key configured)" => _ => Task.FromResult(Raw(HttpStatusCode.NotFound, null)),
        "429" => _ => Task.FromResult(Raw(HttpStatusCode.TooManyRequests, """{"title":"Demasiadas solicitudes","status":429}""", retryAfter: "30")),
        "500" => _ => Task.FromResult(Raw(HttpStatusCode.InternalServerError, """{"title":"x","status":500}""")),
        "413" => _ => Task.FromResult(Raw(HttpStatusCode.RequestEntityTooLarge, null)),
        "400" => _ => Task.FromResult(Raw(HttpStatusCode.BadRequest, """{"title":"x","status":400,"errors":{"path":["Obligatorio"]}}""")),
        "201 html" => _ => Task.FromResult(Raw(HttpStatusCode.Created, "<html><body>not the API</body></html>", "text/html")),
        "201 empty" => _ => Task.FromResult(Raw(HttpStatusCode.Created, null)),
        "201 truncated json" => _ => Task.FromResult(Raw(HttpStatusCode.Created, """{"code":"ERR-2026""", "application/json")),
        "201 json without a code" => _ => Task.FromResult(Raw(HttpStatusCode.Created, """{"persisted":true}""", "application/json")),
        "201 json with an invalid code" => _ => Task.FromResult(Raw(HttpStatusCode.Created, """{"code":"<script>alert(1)</script>","persisted":true}""", "application/json")),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    public static TheoryData<string> ReportFailures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in ReportFailureNames)
            {
                data.Add(name);
            }

            return data;
        }
    }

    /// <summary>Failures of the other API calls: the ones that make a page show the "service unavailable" state, with no code from the API.</summary>
    public static readonly IReadOnlyList<string> UpstreamFailureNames =
    [
        "connection refused", "timeout", "500 without a code", "502 html", "503 empty", "504 empty", "200 html", "200 empty", "200 truncated json",
    ];

    public static Func<BackendRequest, Task<HttpResponseMessage?>> UpstreamFailure(string name) => name switch
    {
        "connection refused" => request => Refused(request)!,
        "timeout" => _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout (simulated).", new TimeoutException()),
        "500 without a code" => _ => Task.FromResult<HttpResponseMessage?>(Raw(HttpStatusCode.InternalServerError, """{"title":"An unexpected error occurred.","status":500}""")),
        "502 html" => _ => Task.FromResult<HttpResponseMessage?>(Raw(HttpStatusCode.BadGateway, "<html><body>Bad gateway</body></html>", "text/html")),
        "503 empty" => _ => Task.FromResult<HttpResponseMessage?>(Raw(HttpStatusCode.ServiceUnavailable, null)),
        "504 empty" => _ => Task.FromResult<HttpResponseMessage?>(Raw(HttpStatusCode.GatewayTimeout, null)),
        "200 html" => _ => Task.FromResult<HttpResponseMessage?>(Raw(HttpStatusCode.OK, "<html><body>not the API</body></html>", "text/html")),
        "200 empty" => _ => Task.FromResult<HttpResponseMessage?>(Raw(HttpStatusCode.OK, null)),
        "200 truncated json" => _ => Task.FromResult<HttpResponseMessage?>(Raw(HttpStatusCode.OK, """{"items":[{"id":""", "application/json")),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    public static TheoryData<string> UpstreamFailures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in UpstreamFailureNames)
            {
                data.Add(name);
            }

            return data;
        }
    }
}

/// <summary>A JWT the Web accepts (it reads only <c>exp</c>; the real API alone checks signatures).</summary>
internal static class FakeJwt
{
    public static string Create(Guid userId, Guid tenantId, DateTimeOffset? expires = null)
    {
        static string Part(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Part(new { alg = "HS256", typ = "JWT" });
        var payload = Part(new { sub = userId, tenant_id = tenantId, exp = (expires ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeSeconds() });
        return $"{header}.{payload}.{Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    /// <summary>The user and tenant a token made by <see cref="Create"/> carries; the default person when the token is absent or not ours.</summary>
    public static (Guid UserId, Guid TenantId) IdentityOf(string? token)
    {
        try
        {
            var payload = token!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return (document.RootElement.GetProperty("sub").GetGuid(), document.RootElement.GetProperty("tenant_id").GetGuid());
        }
        catch (Exception exception) when (exception is NullReferenceException or IndexOutOfRangeException or FormatException or JsonException or KeyNotFoundException)
        {
            return (FakeBackend.UserId, FakeBackend.TenantId);
        }
    }
}
