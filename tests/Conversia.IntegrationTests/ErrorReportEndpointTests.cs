using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Conversia.Application.DTOs.Diagnostics;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Conversia.TestSupport;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// <c>POST /api/diagnostics/errors</c>: how the Web, which has no database, reports its own errors. Enabled only with a configured
/// key, guarded by that key (not by a JWT), validated per field in Spanish, size- and rate-limited, sanitized on the server, and
/// always stored as coming from the Web.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ErrorReportEndpointTests
{
    private readonly CustomWebApplicationFactory _shared;
    private readonly ErrorTestHost _host;

    public ErrorReportEndpointTests(CustomWebApplicationFactory shared)
    {
        _shared = shared;
        _host = ErrorTestHost.Default(shared);
    }

    private Task<HttpResponseMessage> PostAsync(object? body, string? key = ErrorTestHost.ReportKey, string? rawBody = null, string? clientIp = null)
    {
        var client = _host.NewClient();
        return client.SendAsync(ReportApi.Request(body, key, clientIp ?? TestClientIp.Next(), rawBody: rawBody));
    }

    private Task<List<ErrorLogRow>> RowsOf(string marker, ErrorTestHost? host = null)
        => ErrorLogDb.QueryAsync((host ?? _host).ConnectionString, "\"Path\" LIKE @path", new NpgsqlParameter("@path", $"/report/{marker}%"));

    private async Task<ErrorLogRow> AcceptAsync(Dictionary<string, object?> body)
    {
        using var response = await PostAsync(body);
        var (code, persisted) = await ReportApi.ReadCreatedAsync(response);
        Assert.True(persisted);
        return await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
    }

    // ---- the route exists only when a key is configured ----

    [Fact]
    public async Task Without_a_configured_key_the_route_answers_404_and_stores_nothing_while_the_same_request_is_accepted_where_one_is_configured()
    {
        var marker = ErrorTestHost.NewMarker();
        var body = ReportApi.ValidBody(marker);
        using var unconfigured = _shared.CreateClient();

        using var withKey = await unconfigured.SendAsync(ReportApi.Request(body));
        using var withoutKey = await unconfigured.SendAsync(ReportApi.Request(body, key: null));

        Assert.Equal(HttpStatusCode.NotFound, withKey.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withoutKey.StatusCode);
        Assert.Empty(await RowsOf(marker));

        // The very same request, on a host that has a key, is accepted: the 404 above is the "not enabled" answer and not a typo in the route.
        using var configured = await PostAsync(body);
        var (code, _) = await ReportApi.ReadCreatedAsync(configured);
        Assert.Single(await RowsOf(marker), r => r.Code == code);
    }

    // ---- the key ----

    public static TheoryData<string, string?> RejectedKeys => new()
    {
        { "missing header", null },
        { "empty", "" },
        { "wrong, same length", new string('x', ErrorTestHost.ReportKey.Length) },
        { "last character changed", ErrorTestHost.ReportKey[..^1] + "!" },
        { "one character short", ErrorTestHost.ReportKey[..^1] },
        { "one character long", ErrorTestHost.ReportKey + "x" },
        { "upper case", ErrorTestHost.ReportKey.ToUpperInvariant() },
        { "padded with spaces", " " + ErrorTestHost.ReportKey + " " },
        { "much longer", ErrorTestHost.ReportKey + new string('k', 5000) },
        { "only a prefix", ErrorTestHost.ReportKey[..10] },
    };

    [Theory]
    [MemberData(nameof(RejectedKeys))]
    public async Task A_missing_or_wrong_key_is_a_401_without_detail_and_nothing_is_stored(string description, string? key)
    {
        var marker = ErrorTestHost.NewMarker();

        using var response = await PostAsync(ReportApi.ValidBody(marker), key);

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{description}: got {(int)response.StatusCode}");
        await AssertNoDetailAsync(response);
        Assert.Empty(await RowsOf(marker));
        using var control = await PostAsync(ReportApi.ValidBody(marker));
        var (controlCode, _) = await ReportApi.ReadCreatedAsync(control);
        Assert.Equal(controlCode, Assert.Single(await RowsOf(marker)).Code);
    }

    [Fact]
    public async Task The_key_is_only_accepted_in_its_header()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();
        var body = ReportApi.ValidBody(marker);

        var attempts = new List<HttpRequestMessage>();
        attempts.Add(ReportApi.Request(body, key: null, url: $"{ErrorTestHost.ReportUrl}?key={ErrorTestHost.ReportKey}"));
        attempts.Add(ReportApi.Request(body, key: null, url: $"{ErrorTestHost.ReportUrl}?{ReportApi.KeyHeader}={ErrorTestHost.ReportKey}"));
        var bearer = ReportApi.Request(body, key: null);
        bearer.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ErrorTestHost.ReportKey}");
        attempts.Add(bearer);
        var apiKey = ReportApi.Request(body, key: null);
        apiKey.Headers.TryAddWithoutValidation("X-Api-Key", ErrorTestHost.ReportKey);
        attempts.Add(apiKey);
        var inBody = new Dictionary<string, object?>(body) { ["key"] = ErrorTestHost.ReportKey, ["reportKey"] = ErrorTestHost.ReportKey };
        attempts.Add(ReportApi.Request(inBody, key: null));

        foreach (var attempt in attempts)
        {
            using var response = await client.SendAsync(attempt);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Empty(await RowsOf(marker));
    }

    [Fact]
    public async Task A_valid_user_token_does_not_replace_the_key_and_a_garbage_token_does_not_get_in_the_way_of_the_key()
    {
        var marker = ErrorTestHost.NewMarker();
        using var anonymous = _host.NewClient();
        var session = await RegisterTenantSessionAsync(anonymous);
        using var client = _host.NewClient();

        var withToken = ReportApi.Request(ReportApi.ValidBody(marker), key: null);
        withToken.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.OwnerToken}");
        using var unauthorized = await client.SendAsync(withToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Empty(await RowsOf(marker));

        var garbage = ReportApi.Request(ReportApi.ValidBody(marker));
        garbage.Headers.TryAddWithoutValidation("Authorization", "Bearer not.a.jwt");
        using var accepted = await client.SendAsync(garbage);
        await ReportApi.ReadCreatedAsync(accepted);
        Assert.Single(await RowsOf(marker));
    }

    [Fact]
    public async Task Other_methods_on_the_route_are_a_controlled_answer_and_never_data()
    {
        using var client = _host.NewClient();
        using (var exists = await client.SendAsync(ReportApi.Request(ReportApi.ValidBody(ErrorTestHost.NewMarker()))))
        {
            await ReportApi.ReadCreatedAsync(exists); // the route is there: the answers below are about the method, not about a missing route
        }

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, ErrorTestHost.ReportUrl);
            request.Headers.TryAddWithoutValidation(ReportApi.KeyHeader, ErrorTestHost.ReportKey);
            using var response = await client.SendAsync(request);

            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
            await ErrorContract.AssertNotRegisteredAsync(response);
        }
    }

    // ---- accepted reports ----

    [Fact]
    public async Task A_valid_report_is_stored_as_sent_and_answered_with_only_its_code()
    {
        var marker = ErrorTestHost.NewMarker();
        var body = ReportApi.ValidBody(marker);
        var before = DateTime.UtcNow;

        using var response = await PostAsync(body);
        var (code, persisted) = await ReportApi.ReadCreatedAsync(response);

        Assert.True(persisted);
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
        Assert.Equal(code, row.Code);
        Assert.Equal((int)ErrorSource.Web, row.Source);
        Assert.Equal((int)ErrorKind.UpstreamUnavailable, row.Kind);
        Assert.Equal("Production", row.Environment);
        Assert.Equal("1.2.3", row.ServiceVersion);
        Assert.Equal("web-pod-1", row.Instance);
        Assert.Equal("GET", row.HttpMethod);
        Assert.Equal($"/report/{marker}", row.Path);
        Assert.Equal("?page=2&search=tornillo", row.Query);
        Assert.Equal("/products (GET)", row.Endpoint);
        Assert.Equal("{\"page\":\"2\"}", row.RouteValues);
        Assert.Equal(503, row.StatusCode);
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", row.TraceId);
        Assert.Equal($"web-request-{marker}", row.RequestId);
        Assert.Equal(Guid.Parse((string)body["userId"]!), row.UserId);
        Assert.Equal(Guid.Parse((string)body["tenantId"]!), row.TenantId);
        Assert.Equal(marker, row.UserAgent);
        Assert.Equal("System.Net.Http.HttpRequestException", row.ExceptionType);
        Assert.Equal("No se pudo conectar con el API", row.ExceptionMessage);
        Assert.Equal(ReportApi.WebStack, row.ExceptionDetails);
        Assert.Equal("{\"upstream\":\"GET /api/products\",\"elapsedMs\":3012}", row.Context);
        Assert.Equal(ErrorContract.ExpectedFingerprint("System.Net.Http.HttpRequestException", ReportApi.WebTopFrame), row.Fingerprint);
        Assert.InRange(row.OccurredAt, before.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(row.OccurredAt.ToString("yyyyMMdd"), code.Substring(4, 8));
    }

    [Fact]
    public async Task A_report_with_only_the_required_fields_is_accepted()
    {
        var marker = ErrorTestHost.NewMarker();
        var body = new Dictionary<string, object?>
        {
            ["kind"] = "Unhandled",
            ["path"] = $"/report/{marker}",
            ["exceptionType"] = "System.Exception",
        };

        var row = await AcceptAsync(body);

        Assert.Equal((int)ErrorSource.Web, row.Source);
        Assert.Equal((int)ErrorKind.Unhandled, row.Kind);
        Assert.Null(row.UserId);
        Assert.Null(row.TenantId);
        Assert.Matches(ErrorContract.FingerprintPattern, row.Fingerprint!);
    }

    [Theory]
    [InlineData("UpstreamUnavailable", (int)ErrorKind.UpstreamUnavailable)]
    [InlineData("UpstreamBadResponse", (int)ErrorKind.UpstreamBadResponse)]
    [InlineData("Unhandled", (int)ErrorKind.Unhandled)]
    public async Task Each_kind_is_stored_as_itself(string kind, int expected)
    {
        var body = ReportApi.ValidBody(ErrorTestHost.NewMarker());
        body["kind"] = kind;

        var row = await AcceptAsync(body);

        Assert.Equal(expected, row.Kind);
    }

    [Theory]
    [InlineData("\"Api\"")]
    [InlineData("1")]
    [InlineData("\"api\"")]
    [InlineData("null")]
    public async Task The_source_is_always_web_whatever_the_payload_claims(string claimed)
    {
        var marker = ErrorTestHost.NewMarker();
        var body = ReportApi.ValidBody(marker);
        var injectedId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(body);
        var withClaim = json[..^1] + $",\"source\":{claimed},\"Source\":{claimed},\"createdBy\":\"{Guid.NewGuid()}\",\"id\":\"{injectedId}\"}}";

        using var response = await PostAsync(null, rawBody: withClaim);
        var (code, _) = await ReportApi.ReadCreatedAsync(response);

        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
        Assert.Equal((int)ErrorSource.Web, row.Source);
        Assert.Equal(Guid.Parse((string)body["userId"]!), row.UserId); // the payload's own fields are stored...
        Assert.NotEqual(injectedId, row.Id);                          // ...but it cannot choose the row's identity
    }

    [Fact]
    public async Task Secrets_in_whatever_the_web_sends_are_removed_on_the_server()
    {
        const string secret = "S3cr3t-Value-42";
        var marker = ErrorTestHost.NewMarker();
        var body = ReportApi.ValidBody(marker);
        body["query"] = $"?page=2&token={secret}q&password={secret}w";
        body["exceptionMessage"] = $"Falló Server=db;User Id=svc_web;Password={secret}m; fin";
        body["exceptionDetails"] = $"System.Exception: Falló con Authorization: Bearer {secret}d\n   at Conversia.Web.A.B() in /src/A.cs:line 1\ntoken={secret}t";
        body["context"] = $"{{\"authorization\":\"Bearer {secret}c\",\"upstream\":\"GET /api/products\"}}";
        body["userAgent"] = $"{marker} secret={secret}u";
        body["routeValues"] = $"{{\"id\":\"1\",\"apikey\":\"{secret}r\"}}";
        body["endpoint"] = $"/products?client_secret={secret}e";
        body["requestId"] = $"req {marker} \u0000\u0007 api-key={secret}i";

        var row = await AcceptAsync(body);

        row.AssertNowhereInRow(secret, "svc_web", "\0", "\u0007");
        Assert.Contains("page=2", row.Query);
        Assert.Contains("Falló", row.ExceptionMessage);
        Assert.Contains("Conversia.Web.A.B", row.ExceptionDetails);
        Assert.Contains("upstream", row.Context);
        Assert.StartsWith(marker, row.UserAgent);
    }

    [Fact]
    public async Task Markup_in_what_the_web_sends_is_stored_verbatim_because_sanitizing_is_about_secrets_not_html()
    {
        // Stored-XSS caveat: the table holds whatever text the Web reported, markup included. Any view of ErrorLogs (a grid, a page,
        // an export to HTML) must HTML-encode what it shows; the docs state it. Here the contract is only that nothing is rewritten.
        const string markup = "<script>alert(1)</script>";
        var marker = ErrorTestHost.NewMarker();
        var body = ReportApi.ValidBody(marker);
        body["exceptionMessage"] = markup;
        body["userAgent"] = markup;

        var row = await AcceptAsync(body);

        Assert.Equal(markup, row.ExceptionMessage);
        Assert.Equal(markup, row.UserAgent);
    }

    [Fact]
    public async Task With_logging_disabled_a_valid_report_is_answered_201_with_persisted_false_and_nothing_is_stored()
    {
        var disabled = _shared.GetOrAddHost("error-registry:report-disabled", () => ErrorTestHost.Create(
            _shared,
            ErrorTestHost.Settings(
                ("ErrorLogging:Enabled", "false"),
                ("ErrorLogging:ReportKey", ErrorTestHost.ReportKey),
                ("ErrorLogging:ReportRateLimitPerMinute", "1000000"))));
        var marker = ErrorTestHost.NewMarker();
        using var client = disabled.NewClient();

        using var response = await client.SendAsync(ReportApi.Request(ReportApi.ValidBody(marker), clientIp: TestClientIp.Next()));

        var (code, persisted) = await ReportApi.ReadCreatedAsync(response);
        Assert.False(persisted);
        Assert.Empty(await RowsOf(marker, disabled));
        Assert.Null(await ErrorLogDb.FindByCodeAsync(disabled.ConnectionString, code));

        // The very same report on the enabled host is stored, so the empty table above is the switch and not a broken endpoint.
        using var enabled = await PostAsync(ReportApi.ValidBody(marker));
        var (enabledCode, enabledPersisted) = await ReportApi.ReadCreatedAsync(enabled);
        Assert.True(enabledPersisted);
        Assert.Equal(enabledCode, Assert.Single(await RowsOf(marker)).Code);
    }

    // ---- codes ----

    [Fact]
    public async Task A_valid_code_the_web_already_showed_to_the_user_is_honored()
    {
        var code = ErrorContract.NewCode();
        var body = ReportApi.ValidBody(ErrorTestHost.NewMarker());
        body["code"] = code;

        using var response = await PostAsync(body);
        var (returned, persisted) = await ReportApi.ReadCreatedAsync(response);

        Assert.Equal(code, returned);
        Assert.True(persisted);
        Assert.Equal(code, (await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code)).Code);
    }

    [Theory]
    [InlineData("ERR-1")]
    [InlineData("err-20261018-abcdef")]
    [InlineData("ERR-20261018-ABCDEI")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("ERR-20261018-ABCDEF'; DROP TABLE ErrorLogs;--")]
    public async Task An_invalid_code_is_replaced_by_a_new_valid_one(string given)
    {
        var marker = ErrorTestHost.NewMarker();
        var body = ReportApi.ValidBody(marker);
        body["code"] = given;

        using var response = await PostAsync(body);
        var (returned, persisted) = await ReportApi.ReadCreatedAsync(response);

        Assert.NotEqual(given, returned);
        Assert.True(persisted);
        Assert.Equal(marker, (await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, returned)).UserAgent);
        Assert.Null(await ErrorLogDb.FindByCodeAsync(_host.ConnectionString, given));
    }

    [Fact]
    public async Task A_code_that_is_already_used_is_replaced_and_the_existing_row_is_untouched()
    {
        var taken = ErrorContract.NewCode();
        await ErrorLogDb.InsertAsync(_host.ConnectionString, new SeedErrorRow(taken, DateTime.UtcNow, Path: "/planted-by-the-test"));
        var body = ReportApi.ValidBody(ErrorTestHost.NewMarker());
        body["code"] = taken;

        using var response = await PostAsync(body);
        var (returned, persisted) = await ReportApi.ReadCreatedAsync(response);

        Assert.NotEqual(taken, returned);
        Assert.True(persisted);
        Assert.Equal("/planted-by-the-test", (await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, taken)).Path);
        Assert.Equal((int)ErrorSource.Web, (await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, returned)).Source);
    }

    [Fact]
    public async Task Parallel_reports_asking_for_one_code_give_it_to_one_and_all_get_stored()
    {
        var contested = ErrorContract.NewCode();
        var marker = ErrorTestHost.NewMarker();

        var codes = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            var body = ReportApi.ValidBody(marker);
            body["code"] = contested;
            body["exceptionMessage"] = $"racer {i}";
            using var response = await PostAsync(body);
            return (await ReportApi.ReadCreatedAsync(response)).Code;
        }));

        Assert.Equal(1, codes.Count(c => c == contested));
        Assert.Equal(10, codes.Distinct().Count());
        Assert.Equal(10, (await RowsOf(marker)).Count);
    }

    [Fact]
    public async Task Thirty_parallel_reports_get_thirty_codes_and_thirty_rows()
    {
        var marker = ErrorTestHost.NewMarker();

        var codes = await Task.WhenAll(Enumerable.Range(0, 30).Select(async i =>
        {
            var body = ReportApi.ValidBody(marker);
            body["exceptionMessage"] = $"parallel report {i}";
            using var response = await PostAsync(body);
            return (await ReportApi.ReadCreatedAsync(response)).Code;
        }));

        Assert.Equal(30, codes.Distinct().Count());
        Assert.Equal(codes.Order(), (await RowsOf(marker)).Select(r => r.Code).Order());
    }

    // ---- validation ----

    public static TheoryData<string, int> FieldLimits
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var (name, limit) in ErrorContract.RefusedWhenLongerReportFields)
            {
                data.Add(name, limit);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FieldLimits))]
    public async Task A_value_over_its_limit_is_a_spanish_400_under_its_field_and_nothing_is_stored(string field, int limit)
    {
        var marker = ErrorTestHost.NewMarker();
        var body = ReportApi.ValidBody(marker);
        var echo = $"ECHO{marker}";
        body[field] = field == "path" ? $"/report/{marker}" + new string('p', limit + 1) : echo + new string('x', limit + 1);

        using var response = await PostAsync(body);

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{field} with {limit + 1} characters: got {(int)response.StatusCode}: {text}");
        Assert.DoesNotContain(echo, text);
        var report = new ApiMessageReport();
        await report.CheckAsync(field, response, HttpStatusCode.BadRequest, validationProblem: true);
        report.AssertClean();
        var problem = JsonSerializer.Deserialize<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains(problem!.Errors.Keys, key => key.Contains(field, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await RowsOf(marker));

        using var control = await PostAsync(ReportApi.ValidBody(marker));
        await ReportApi.ReadCreatedAsync(control);
        Assert.Single(await RowsOf(marker));
    }

    [Fact]
    public async Task Values_exactly_at_their_limits_are_accepted_and_stored_whole()
    {
        var marker = ErrorTestHost.NewMarker();
        var body = ReportApi.ValidBody(marker);
        foreach (var (column, limit) in ErrorContract.StoredLimits)
        {
            body[char.ToLowerInvariant(column[0]) + column[1..]] = new string('a', limit);
        }

        var pathLimit = ErrorContract.TextColumns.Single(c => c.Column == "Path").Length;
        body["path"] = $"/report/{marker}" + new string('p', pathLimit - $"/report/{marker}".Length);

        var row = await AcceptAsync(body);

        foreach (var (column, limit) in ErrorContract.StoredLimits)
        {
            var stored = (string?)typeof(ErrorLogRow).GetProperty(column)!.GetValue(row);
            Assert.True(stored is not null && stored.Length == limit, $"{column} should be stored whole ({limit} characters) but has {stored?.Length}.");
        }
    }

    [Fact]
    public async Task Exception_details_beyond_the_cap_are_either_refused_in_spanish_or_stored_cut_never_an_unhandled_failure()
    {
        var marker = ErrorTestHost.NewMarker();
        var body = ReportApi.ValidBody(marker);
        body["exceptionDetails"] = "System.Exception: long\n" + string.Concat(Enumerable.Repeat("   at Conversia.Web.Deep.Frame() in /src/F.cs:line 1\n", 600));

        using var response = await PostAsync(body);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.BadRequest });
        if (response.StatusCode == HttpStatusCode.Created)
        {
            var (code, _) = await ReportApi.ReadCreatedAsync(response);
            var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
            Assert.True(row.ExceptionDetails!.Length <= 16000);
            Assert.EndsWith(Conversia.Application.ErrorLogging.ErrorSanitizer.TruncationMarker, row.ExceptionDetails);
        }
        else
        {
            var report = new ApiMessageReport();
            await report.CheckAsync("details", response, HttpStatusCode.BadRequest, validationProblem: true);
            report.AssertClean();
            Assert.Empty(await RowsOf(marker));
        }
    }

    public static TheoryData<string> RequiredFields => new() { "kind", "path", "exceptionType" };

    [Theory]
    [MemberData(nameof(RequiredFields))]
    public async Task A_missing_null_empty_or_blank_required_field_is_a_spanish_400_under_that_field(string field)
    {
        var marker = ErrorTestHost.NewMarker();
        foreach (var mode in new[] { "missing", "null", "empty", "blank" })
        {
            var body = ReportApi.ValidBody(marker);
            if (mode == "missing")
            {
                body.Remove(field);
            }
            else
            {
                body[field] = mode switch { "null" => null, "empty" => string.Empty, _ => "   " };
            }

            using var response = await PostAsync(body);

            var report = new ApiMessageReport();
            await report.CheckAsync($"{field} {mode}", response, HttpStatusCode.BadRequest, validationProblem: true);
            report.AssertClean();
            var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>();
            Assert.Contains(problem!.Errors.Keys, key => key.Contains(field, StringComparison.OrdinalIgnoreCase)); // "Kind" or "$.kind"
        }

        Assert.Empty(await RowsOf(marker));
    }

    public static TheoryData<string, string> WrongTypesAndValues => new()
    {
        { "kind", "\"Nope\"" }, { "kind", "99" }, { "kind", "0" }, { "kind", "-1" }, { "kind", "\"Api\"" }, { "kind", "[1]" },
        { "userId", "\"not-a-guid\"" }, { "userId", "\"00000000-0000-0000-0000-00000000000Z\"" }, { "userId", "12345" },
        { "tenantId", "\"not-a-guid\"" }, { "tenantId", "\"{}\"" }, { "tenantId", "true" },
        { "statusCode", "\"abc\"" }, { "statusCode", "1.5" }, { "statusCode", "{\"a\":1}" },
        { "occurredAt", "\"yesterday\"" }, { "occurredAt", "\"2026-13-45\"" }, { "occurredAt", "12345678901234567890" },
    };

    [Theory]
    [MemberData(nameof(WrongTypesAndValues))]
    public async Task A_value_of_the_wrong_type_or_an_unknown_kind_is_a_spanish_400_and_nothing_is_stored(string field, string jsonValue)
    {
        var marker = ErrorTestHost.NewMarker();
        var body = ReportApi.ValidBody(marker);
        body[field] = JsonDocument.Parse(jsonValue).RootElement.Clone();
        var value = jsonValue;

        using var response = await PostAsync(body);

        var report = new ApiMessageReport();
        await report.CheckAsync($"{field}={value}", response, HttpStatusCode.BadRequest);
        report.AssertClean();
        Assert.Empty(await RowsOf(marker));
    }

    public static TheoryData<string, string> BrokenBodies => new()
    {
        { "", "empty body" },
        { "null", "JSON null" },
        { "[]", "an array" },
        { "42", "a number" },
        { "\"just text\"", "a string" },
        { "{not json", "malformed JSON" },
        { "{}", "an empty object" },
        { "{\"kind\":", "truncated JSON" },
    };

    [Theory]
    [MemberData(nameof(BrokenBodies))]
    public async Task A_body_that_is_not_a_report_is_a_spanish_400(string raw, string description)
    {
        using var response = await PostAsync(null, rawBody: raw);

        var report = new ApiMessageReport();
        await report.CheckAsync(description, response, HttpStatusCode.BadRequest);
        report.AssertClean();
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_refused_in_spanish_and_nothing_is_stored()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();
        using var request = ReportApi.Request(null, rawBody: JsonSerializer.Serialize(ReportApi.ValidBody(marker)));
        request.Content = new StringContent(JsonSerializer.Serialize(ReportApi.ValidBody(marker)), Encoding.UTF8, "text/plain");

        using var response = await client.SendAsync(request);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.UnsupportedMediaType });
        var report = new ApiMessageReport();
        await report.CheckAsync("text/plain", response, response.StatusCode, requireExplanation: false);
        report.AssertClean();
        Assert.Empty(await RowsOf(marker));
    }

    // ---- size ----

    [Fact]
    public async Task A_body_over_64_kilobytes_is_a_413_in_spanish_and_nothing_is_stored_while_one_just_under_it_is_accepted()
    {
        var marker = ErrorTestHost.NewMarker();
        var small = ReportApi.ValidBody(marker);
        small["padding"] = new string('p', 40 * 1024);
        var oversized = ReportApi.ValidBody(marker);
        oversized["padding"] = new string('p', 70 * 1024);

        using var accepted = await PostAsync(small);
        await ReportApi.ReadCreatedAsync(accepted);
        var stored = Assert.Single(await RowsOf(marker));
        stored.AssertNowhereInRow(new string('p', 64)); // the unknown "padding" field is read and dropped, it is in no column

        using var rejected = await PostAsync(oversized);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        var report = new ApiMessageReport();
        await report.CheckAsync("413", rejected, HttpStatusCode.RequestEntityTooLarge);
        report.AssertClean();
        Assert.Single(await RowsOf(marker)); // still only the accepted one
    }

    [Fact]
    public async Task An_oversized_body_without_a_valid_key_is_still_refused_and_stores_nothing()
    {
        var marker = ErrorTestHost.NewMarker();
        var oversized = ReportApi.ValidBody(marker);
        oversized["padding"] = new string('p', 70 * 1024);

        using var response = await PostAsync(oversized, key: "wrong");

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.RequestEntityTooLarge });
        Assert.Empty(await RowsOf(marker));
    }

    // ---- rate limit ----

    private ErrorTestHost Limited() => _shared.GetOrAddHost("error-registry:report-limited", () => ErrorTestHost.Create(
        _shared,
        ErrorTestHost.Settings(
            ("ErrorLogging:ReportKey", ErrorTestHost.ReportKey),
            ("ErrorLogging:ReportRateLimitPerMinute", "3"),
            ("RateLimiting:Login:PermitLimit", "1"))));

    [Fact]
    public async Task After_the_budget_a_report_is_a_429_with_retry_after_and_is_not_stored_while_another_address_is_unaffected()
    {
        var limited = Limited();
        var marker = ErrorTestHost.NewMarker();
        var ip = TestClientIp.Next();
        using var client = limited.NewClient();

        for (var i = 0; i < 3; i++)
        {
            using var ok = await client.SendAsync(ReportApi.Request(ReportApi.ValidBody(marker), clientIp: ip));
            await ReportApi.ReadCreatedAsync(ok);
        }

        using var throttled = await client.SendAsync(ReportApi.Request(ReportApi.ValidBody(marker), clientIp: ip));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.TryGetValues("Retry-After", out var retryAfter), "A 429 must carry Retry-After.");
        var seconds = Assert.Single(retryAfter!);
        Assert.Matches(new Regex(@"^\d+$"), seconds);
        Assert.InRange(int.Parse(seconds), 1, 60);
        var report = new ApiMessageReport();
        await report.CheckAsync("429", throttled, HttpStatusCode.TooManyRequests);
        report.AssertClean();
        Assert.Equal(3, (await RowsOf(marker, limited)).Count);

        using var otherAddress = await client.SendAsync(ReportApi.Request(ReportApi.ValidBody(marker), clientIp: TestClientIp.Next()));
        await ReportApi.ReadCreatedAsync(otherAddress);
        Assert.Equal(4, (await RowsOf(marker, limited)).Count);
    }

    [Fact]
    public async Task A_flood_of_wrong_keys_runs_into_the_rate_limit_so_the_key_cannot_be_guessed_at_full_speed()
    {
        var limited = Limited();
        var marker = ErrorTestHost.NewMarker();
        var ip = TestClientIp.Next();
        using var client = limited.NewClient();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 6; i++)
        {
            using var response = await client.SendAsync(ReportApi.Request(ReportApi.ValidBody(marker), key: $"wrong-key-{i}", clientIp: ip));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.All(statuses, status => Assert.Contains(status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests }));
        Assert.Empty(await RowsOf(marker, limited));

        using var otherAddress = await client.SendAsync(ReportApi.Request(ReportApi.ValidBody(marker), clientIp: TestClientIp.Next()));
        await ReportApi.ReadCreatedAsync(otherAddress); // the flood is one address' problem
    }

    [Fact]
    public async Task The_report_budget_and_the_login_budget_are_separate()
    {
        var limited = Limited();
        var marker = ErrorTestHost.NewMarker();
        var loginIp = TestClientIp.Next();
        var reportIp = TestClientIp.Next();
        using var client = limited.NewClient();

        Task<HttpResponseMessage> MalformedLogin(string ip)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = new StringContent("{not json", Encoding.UTF8, "application/json") };
            request.Headers.Add(TestClientIp.HeaderName, ip);
            return client.SendAsync(request);
        }

        // Spend the whole login budget (1) of one address: the report endpoint is still open to it.
        using (await MalformedLogin(loginIp))
        {
        }

        using var loginThrottled = await MalformedLogin(loginIp);
        Assert.Equal(HttpStatusCode.TooManyRequests, loginThrottled.StatusCode);
        using var reportAfterLogins = await client.SendAsync(ReportApi.Request(ReportApi.ValidBody(marker), clientIp: loginIp));
        await ReportApi.ReadCreatedAsync(reportAfterLogins);

        // Spend the whole report budget (3) of another address: logins are still open to it.
        for (var i = 0; i < 3; i++)
        {
            using var ok = await client.SendAsync(ReportApi.Request(ReportApi.ValidBody(marker), clientIp: reportIp));
            await ReportApi.ReadCreatedAsync(ok);
        }

        using var reportThrottled = await client.SendAsync(ReportApi.Request(ReportApi.ValidBody(marker), clientIp: reportIp));
        Assert.Equal(HttpStatusCode.TooManyRequests, reportThrottled.StatusCode);
        using var loginAfterReports = await MalformedLogin(reportIp);
        Assert.Equal(HttpStatusCode.BadRequest, loginAfterReports.StatusCode);
    }

    // ---- never recursing, never failing the Web ----

    [Fact]
    public async Task A_failure_inside_the_endpoint_ends_in_the_normal_generic_500_with_a_code_and_nothing_more()
    {
        var registry = new ThrowOnceRegistry();
        var host = ErrorTestHost.Create(
            _shared,
            ErrorTestHost.Settings(("ErrorLogging:ReportKey", ErrorTestHost.ReportKey)),
            configureServices: (services, _) =>
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IErrorRegistry)).ToList())
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<IErrorRegistry>(registry);
            });
        try
        {
            var marker = ErrorTestHost.NewMarker();
            using var client = host.NewClient();

            using var response = await client.SendAsync(ReportApi.Request(ReportApi.ValidBody(marker)));

            var code = await ErrorContract.AssertUnexpectedAsync(response, "the registry itself failed");
            Assert.Equal(registry.SecondCode, code);
            var reports = registry.Reports.ToArray();
            Assert.Equal(2, reports.Length); // the endpoint's own report, then the middleware's one for the failure: no loop
            Assert.Equal(ErrorSource.Web, reports[0].Source);
            Assert.Equal(ErrorSource.Api, reports[1].Source);
            Assert.Equal(ErrorKind.Unhandled, reports[1].Kind);
            Assert.Equal(ErrorTestHost.ReportUrl, reports[1].Path);
            Assert.Equal("System.InvalidOperationException", reports[1].ExceptionType);
        }
        finally
        {
            host.Factory.Dispose();
        }
    }

    [Fact]
    public async Task When_the_database_is_down_the_report_is_still_answered_with_a_code_and_persisted_false()
    {
        var host = ErrorTestHost.Create(
            _shared,
            ErrorTestHost.Settings(("ErrorLogging:ReportKey", ErrorTestHost.ReportKey), ("ErrorLogging:PersistTimeoutSeconds", "2")),
            DemoHostSettings.UnreachableConnectionString);
        try
        {
            var marker = ErrorTestHost.NewMarker();
            using var client = host.NewClient();

            using var response = await client.SendAsync(ReportApi.Request(ReportApi.ValidBody(marker)));

            var (code, persisted) = await ReportApi.ReadCreatedAsync(response);
            Assert.False(persisted);
            Assert.Contains(host.Logs.Mentioning(code), e => e.Level == LogLevel.Error && e.Mentions($"/report/{marker}"));
        }
        finally
        {
            host.Factory.Dispose();
        }
    }

    private static async Task AssertNoDetailAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ErrorTestHost.ReportKey, text);
        if (text.Length == 0 || response.Content.Headers.ContentType?.MediaType != "application/problem+json")
        {
            return;
        }

        using var document = JsonDocument.Parse(text);
        var hasDetail = document.RootElement.TryGetProperty("detail", out var detail) && !string.IsNullOrWhiteSpace(detail.GetString());
        Assert.False(hasDetail, $"A 401 should not explain why: {text}");
    }

    private sealed class ThrowOnceRegistry : IErrorRegistry
    {
        private int _calls;

        public ConcurrentQueue<ErrorReport> Reports { get; } = new();

        public string SecondCode { get; } = ErrorContract.NewCode();

        public Task<ErrorRegistration> RegisterAsync(ErrorReport report, CancellationToken cancellationToken = default)
        {
            Reports.Enqueue(report);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("the registry itself failed");
            }

            return Task.FromResult(new ErrorRegistration(SecondCode, true));
        }
    }
}
