using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Conversia.TestSupport;

namespace Conversia.IntegrationTests;

/// <summary>What every unexpected-error answer of the API, and every report answer, must look like.</summary>
internal static class ErrorContract
{
    public static Regex CodePattern => ErrorLimits.CodePattern;

    public static readonly Regex FingerprintPattern = new("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);

    public const string HeaderName = "X-Error-Id";

    /// <summary>The text columns of <c>ErrorLogs</c> with their limits; shared with the Web suite (<c>tests/Shared/ErrorLimits.cs</c>).</summary>
    public static IReadOnlyList<(string Column, int Length)> TextColumns => ErrorLimits.TextColumns;

    /// <summary><c>ExceptionDetails</c> is <c>nvarchar(max)</c>; the requirement caps the text that is stored in it.</summary>
    public const int ExceptionDetailsCap = ErrorLimits.ExceptionDetailsCap;

    /// <summary>Every value a registration stores (all text columns but the code, which the registry owns, plus the capped details).</summary>
    public static IEnumerable<(string Column, int Limit)> StoredLimits
        => TextColumns.Where(c => c.Column != "Code").Select(c => (c.Column, c.Length)).Append(("ExceptionDetails", ExceptionDetailsCap));

    /// <summary>The fields of a report that are refused (400) when longer than their limit, by their JSON name.</summary>
    public static IEnumerable<(string Field, int Limit)> RefusedWhenLongerReportFields => ErrorLimits.RefusedWhenLongerReportFields;

    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A well-formed code for today, built here so that tests do not depend on the helper under test.</summary>
    public static string NewCode(DateTime? utcNow = null)
    {
        var suffix = new string(Enumerable.Range(0, 6).Select(_ => CrockfordAlphabet[RandomNumberGenerator.GetInt32(CrockfordAlphabet.Length)]).ToArray());
        return $"ERR-{(utcNow ?? DateTime.UtcNow):yyyyMMdd}-{suffix}";
    }

    /// <summary>SHA-256 of <c>type|frame</c>, first 16 lowercase hex characters, spelled out independently of the helper under test.</summary>
    public static string ExpectedFingerprint(string type, string frame)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(type + "|" + frame))).ToLowerInvariant()[..16];

    /// <summary>
    /// Asserts the whole 500 contract (status, media type, unchanged Spanish title and detail, the code in <c>errorId</c> and in
    /// <c>X-Error-Id</c>, nothing internal in the body) and returns the code.
    /// </summary>
    public static async Task<string> AssertUnexpectedAsync(HttpResponseMessage response, params string[] mustNotAppearInBody)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.InternalServerError, $"Expected 500 but got {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal(ApiMessages.UnexpectedTitle, root.GetProperty("title").GetString());
        Assert.Equal(ApiMessages.Unexpected, root.GetProperty("detail").GetString());

        Assert.True(root.TryGetProperty("errorId", out var errorId), $"The body has no errorId: {body}");
        Assert.Equal(JsonValueKind.String, errorId.ValueKind);
        var code = errorId.GetString()!;
        Assert.Matches(CodePattern, code);

        Assert.True(response.Headers.TryGetValues(HeaderName, out var headerValues), $"The response has no {HeaderName} header.");
        Assert.Equal(code, Assert.Single(headerValues!));

        TestHelpers.AssertNoInternalsLeaked(body);
        Assert.DoesNotContain("traceId", body, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in mustNotAppearInBody)
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }

        var texts = ApiMessageReport.TextsOf(body);
        Assert.All(texts, t => Assert.Empty(EnglishLeaks.Find(t.Text)));
        return code;
    }

    /// <summary>Asserts the answer is a handled one: no code, no header, nothing that says "unexpected".</summary>
    public static async Task AssertNotRegisteredAsync(HttpResponseMessage response)
    {
        Assert.False(response.Headers.Contains(HeaderName), $"A handled answer carries {HeaderName}.");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("errorId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiMessages.Unexpected, body);
    }

    /// <summary>
    /// The requirement says the error that could not be stored is logged with its code, path, exception type, fingerprint and message:
    /// at least one Error entry must carry all of them together (an implementation may log the failure more than once).
    /// </summary>
    public static void AssertLoggedWithKeyFields(LogCapture logs, string code, params string[] keyFields)
    {
        var errors = logs.Mentioning(code).Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error).ToList();
        Assert.True(
            errors.Any(e => keyFields.All(e.Mentions)),
            $"No Error log carries the code {code} together with {string.Join(", ", keyFields)}. Error entries with the code: {string.Join(" | ", errors.Select(e => e.FullText))}");
    }

    /// <summary>The route values JSON of a row as a dictionary.</summary>
    public static Dictionary<string, string> ParseRouteValues(string? json)
    {
        Assert.False(string.IsNullOrWhiteSpace(json), "RouteValues is empty.");
        using var document = JsonDocument.Parse(json!);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Builds the calls to <c>POST /api/diagnostics/errors</c> the Web makes.</summary>
internal static class ReportApi
{
    public const string KeyHeader = "X-Error-Report-Key";

    public static readonly string WebStack =
        "System.Net.Http.HttpRequestException: Connection refused (localhost:5000)\n"
        + "   at System.Net.Http.HttpConnectionPool.ConnectAsync(HttpRequestMessage request)\n"
        + "   at Conversia.Web.Services.ApiClient.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) in /src/Services/ApiClient.cs:line 88\n"
        + "   at Conversia.Web.Components.Pages.Products.OnInitializedAsync() in /src/Products.razor:line 30";

    public const string WebTopFrame = "Conversia.Web.Services.ApiClient.SendAsync";

    /// <summary>A report as the Web would send it, every optional field filled and recognisable.</summary>
    public static Dictionary<string, object?> ValidBody(string marker) => new()
    {
        ["kind"] = "UpstreamUnavailable",
        ["httpMethod"] = "GET",
        ["path"] = $"/report/{marker}",
        ["query"] = "?page=2&search=tornillo",
        ["endpoint"] = "/products (GET)",
        ["routeValues"] = "{\"page\":\"2\"}",
        ["statusCode"] = 503,
        ["traceId"] = "4bf92f3577b34da6a3ce929d0e0e4736",
        ["requestId"] = $"web-request-{marker}",
        ["userId"] = Guid.NewGuid().ToString(),
        ["tenantId"] = Guid.NewGuid().ToString(),
        ["userAgent"] = marker,
        ["exceptionType"] = "System.Net.Http.HttpRequestException",
        ["exceptionMessage"] = "No se pudo conectar con el API",
        ["exceptionDetails"] = WebStack,
        ["context"] = "{\"upstream\":\"GET /api/products\",\"elapsedMs\":3012}",
        ["environment"] = "Production",
        ["serviceVersion"] = "1.2.3",
        ["instance"] = "web-pod-1",
        ["occurredAt"] = DateTime.UtcNow.ToString("O"),
    };

    public static HttpRequestMessage Request(
        object? body, string? key = ErrorTestHost.ReportKey, string? clientIp = null, string url = ErrorTestHost.ReportUrl, string? rawBody = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(rawBody ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(KeyHeader, key);
        }

        if (clientIp is not null)
        {
            request.Headers.TryAddWithoutValidation(TestClientIp.HeaderName, clientIp);
        }

        return request;
    }

    /// <summary>Asserts a <c>201</c> with exactly <c>{ code, persisted }</c> and returns both.</summary>
    public static async Task<(string Code, bool Persisted)> ReadCreatedAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201 but got {(int)response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        var properties = document.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "code", "persisted" }, properties);
        var code = document.RootElement.GetProperty("code").GetString()!;
        Assert.Matches(ErrorContract.CodePattern, code);
        return (code, document.RootElement.GetProperty("persisted").GetBoolean());
    }
}
