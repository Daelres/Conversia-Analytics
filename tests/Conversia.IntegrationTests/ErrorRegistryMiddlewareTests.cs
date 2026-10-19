using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Conversia.Domain.Enums;
using Conversia.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Requirement "error-registry", exception middleware: the generic catch records the unexpected error with a code, answers the
/// unchanged Spanish 500 plus <c>errorId</c> / <c>X-Error-Id</c>, and never registers a known outcome or a client-caused one.
/// Every failure here is a real request that fails inside the real use case (see <see cref="FaultInjection"/>); every assertion on a
/// stored row is preceded by "the request really failed and the row was found by the code the response announced".
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ErrorRegistryMiddlewareTests
{
    private const string MiddlewareCategory = "ExceptionHandlingMiddleware";

    private readonly CustomWebApplicationFactory _shared;
    private readonly ErrorTestHost _host;

    public ErrorRegistryMiddlewareTests(CustomWebApplicationFactory shared)
    {
        _shared = shared;
        _host = ErrorTestHost.Default(shared);
    }

    // ---- the response contract ----

    [Fact]
    public async Task An_unexpected_failure_answers_the_unchanged_spanish_500_plus_the_code_in_the_body_and_in_a_header()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();
        const string secretMessage = "internal-detail-that-must-not-reach-the-caller";

        using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, message: secretMessage));

        var code = await ErrorContract.AssertUnexpectedAsync(response, secretMessage, "InvalidOperationException", "RaiseInvalidOperation");
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
        Assert.Equal(marker, row.UserAgent);
    }

    [Fact]
    public async Task An_anonymous_failure_is_stored_without_a_user_or_a_tenant()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();

        using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker));

        var code = await ErrorContract.AssertUnexpectedAsync(response);
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
        Assert.Null(row.UserId);
        Assert.Null(row.TenantId);
        Assert.Equal("POST", row.HttpMethod);
        Assert.Equal("/api/auth/login", row.Path);
        Assert.Contains("Conversia.API.Controllers.AuthController.Login", row.Endpoint);
        Assert.Equal(500, row.StatusCode);
    }

    // ---- everything stored matches the request that caused it ----

    [Fact]
    public async Task Every_stored_field_matches_the_request_that_caused_the_failure()
    {
        using var anonymous = _host.NewClient();
        var session = await RegisterTenantSessionAsync(anonymous);
        var conversationId = Guid.NewGuid();
        var marker = ErrorTestHost.NewMarker();
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        var request = ErrorTestHost.FailingAdvisorAssignment(
            conversationId, marker, session.OwnerToken, message: "stored failure", query: "?search=tornillo&token=QUERY-SECRET-" + marker + "&page=2");
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
        using var client = _host.NewClient();
        var before = DateTime.UtcNow;

        using var response = await client.SendAsync(request);
        var after = DateTime.UtcNow;

        var code = await ErrorContract.AssertUnexpectedAsync(response);
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
        var recorded = await _host.Recorder.CompletedAsync(marker);

        Assert.Equal(code, row.Code);
        Assert.Equal((int)ErrorSource.Api, row.Source);
        Assert.Equal((int)ErrorKind.Unhandled, row.Kind);
        Assert.InRange(row.OccurredAt, before.AddSeconds(-5), after.AddSeconds(5));
        Assert.Equal(row.OccurredAt.ToString("yyyyMMdd"), code.Substring(4, 8));
        Assert.NotEqual(default, row.CreatedAt);

        Assert.Equal(_host.Factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName, row.Environment);
        Assert.False(string.IsNullOrWhiteSpace(row.Instance));
        Assert.True(row.Instance!.Length <= 100);
        AssertServiceVersion(row.ServiceVersion);

        Assert.Equal("PUT", row.HttpMethod);
        Assert.Equal($"/api/conversations/{conversationId}/advisor", row.Path);
        Assert.Contains("Conversia.API.Controllers.ConversationsController.AssignAdvisor", row.Endpoint);
        var routeValues = ErrorContract.ParseRouteValues(row.RouteValues);
        Assert.Equal(conversationId.ToString(), routeValues["id"], ignoreCase: true);
        Assert.Equal(500, row.StatusCode);

        Assert.Equal(traceId, row.TraceId, ignoreCase: true);
        Assert.False(string.IsNullOrEmpty(recorded.TraceIdentifier));
        Assert.Equal(recorded.TraceIdentifier, row.RequestId);

        Assert.Equal(session.OwnerId, row.UserId);
        Assert.Equal(session.TenantId, row.TenantId);
        Assert.Equal(marker, row.UserAgent);

        Assert.Contains("search=tornillo", row.Query);
        Assert.Contains("page=2", row.Query);
        Assert.DoesNotContain("QUERY-SECRET", row.Query);
        Assert.Contains("token=", row.Query);

        Assert.Equal("System.InvalidOperationException", row.ExceptionType);
        Assert.Contains("stored failure", row.ExceptionMessage);
        Assert.Contains("System.InvalidOperationException", row.ExceptionDetails);
        Assert.Contains("stored failure", row.ExceptionDetails);
        Assert.Contains("RaiseInvalidOperation", row.ExceptionDetails);
        Assert.Matches(ErrorContract.FingerprintPattern, row.Fingerprint!);
    }

    [Fact]
    public async Task The_stored_rows_are_not_tenant_filtered_and_each_failure_has_its_own_row()
    {
        using var anonymous = _host.NewClient();
        var tenantA = await RegisterTenantSessionAsync(anonymous, "tenant-a");
        var tenantB = await RegisterTenantSessionAsync(anonymous, "tenant-b");
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();

        using var responseA = await client.SendAsync(ErrorTestHost.FailingAdvisorAssignment(Guid.NewGuid(), marker, tenantA.OwnerToken, callId: marker + "-a"));
        using var responseB = await client.SendAsync(ErrorTestHost.FailingAdvisorAssignment(Guid.NewGuid(), marker, tenantB.OwnerToken, callId: marker + "-b"));

        var codeA = await ErrorContract.AssertUnexpectedAsync(responseA);
        var codeB = await ErrorContract.AssertUnexpectedAsync(responseB);
        var rows = await ErrorLogDb.ByUserAgentAsync(_host.ConnectionString, marker);
        Assert.Equal(2, rows.Count);
        Assert.Equal(tenantA.TenantId, rows.Single(r => r.Code == codeA).TenantId);
        Assert.Equal(tenantB.TenantId, rows.Single(r => r.Code == codeB).TenantId);
    }

    [Fact]
    public async Task Exception_text_is_sanitized_before_it_is_stored_but_stays_useful()
    {
        using var anonymous = _host.NewClient();
        var session = await RegisterTenantSessionAsync(anonymous);
        var jwtSignature = session.OwnerToken.Split('.')[2];
        var marker = ErrorTestHost.NewMarker();
        var message = $"Fallo al conectar con Server=db.internal;Database=x;User Id=svc_sql;Password=hunter2; token {session.OwnerToken} y Bearer abc123def456 fin";
        using var client = _host.NewClient();

        using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, message: message));

        var code = await ErrorContract.AssertUnexpectedAsync(response, "hunter2", "svc_sql");
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
        row.AssertNowhereInRow("hunter2", "svc_sql", "abc123def456", jwtSignature, session.OwnerToken);
        Assert.Contains("Fallo al conectar con", row.ExceptionMessage);
        Assert.Contains("Fallo al conectar con", row.ExceptionDetails);
        Assert.Contains("fin", row.ExceptionMessage);
        Assert.Matches(new Regex(@"\*\*\*|\[jwt\]"), row.ExceptionMessage!);
        Assert.Contains("System.InvalidOperationException", row.ExceptionDetails);
        Assert.Contains("RaiseInvalidOperation", row.ExceptionDetails);
    }

    [Fact]
    public async Task Inner_exceptions_and_their_stack_traces_are_part_of_the_stored_details()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();

        using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, FaultInjection.WithInnerException, "outer failure"));

        var code = await ErrorContract.AssertUnexpectedAsync(response);
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
        Assert.Equal("System.InvalidOperationException", row.ExceptionType);
        Assert.Equal("outer failure", row.ExceptionMessage);
        Assert.Contains("System.ArgumentException", row.ExceptionDetails);
        Assert.Contains("inner cause of outer failure", row.ExceptionDetails);
        Assert.Contains("System.FormatException", row.ExceptionDetails);
        Assert.Contains("innermost cause", row.ExceptionDetails);
        Assert.Contains("   at ", row.ExceptionDetails);
        Assert.True(row.ExceptionDetails!.Length <= 16000);
    }

    // ---- what is deliberately NOT stored ----

    [Fact]
    public async Task Bodies_headers_cookies_tokens_and_the_client_address_are_never_stored()
    {
        using var anonymous = _host.NewClient();
        var session = await RegisterTenantSessionAsync(anonymous);
        var marker = ErrorTestHost.NewMarker();
        var clientIp = TestClientIp.Next();
        var bodySecret = $"BODY-SECRET-{marker}";
        var bodyName = $"BODY-NAME-{marker}";
        var headerSecret = $"HEADER-SECRET-{marker}";
        var cookieSecret = $"COOKIE-SECRET-{marker}";
        var request = ErrorTestHost.FailingAdvisorAssignment(Guid.NewGuid(), marker, session.OwnerToken);
        request.Headers.TryAddWithoutValidation("X-Api-Key", headerSecret);
        request.Headers.TryAddWithoutValidation("Cookie", $"session={cookieSecret}");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.77");
        request.Headers.TryAddWithoutValidation("Referer", $"https://example.test/?ref={marker}");
        request.Headers.TryAddWithoutValidation(TestClientIp.HeaderName, clientIp);
        using var client = _host.NewClient();

        using var response = await client.SendAsync(request);

        var code = await ErrorContract.AssertUnexpectedAsync(response);
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
        Assert.Equal(session.OwnerId, row.UserId); // the failing request carried the token and the body
        row.AssertNowhereInRow(
            bodySecret, bodyName, headerSecret, cookieSecret, "203.0.113.77", clientIp, session.OwnerToken,
            session.OwnerToken.Split('.')[2], "BODY-", "COOKIE-", "HEADER-");
        Assert.DoesNotContain("example.test/?ref", string.Join(' ', row.AllValues));
    }

    // ---- fingerprint across requests ----

    [Fact]
    public async Task Repeated_failures_at_one_place_share_a_fingerprint_and_other_places_do_not()
    {
        using var anonymous = _host.NewClient();
        var session = await RegisterTenantSessionAsync(anonymous);
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();

        var codes = new List<string>();
        foreach (var request in new[]
        {
            ErrorTestHost.FailingLogin(marker, FaultInjection.InvalidOperation, "first message 111", marker + "-1"),
            ErrorTestHost.FailingAdvisorAssignment(Guid.NewGuid(), marker, session.OwnerToken, FaultInjection.InvalidOperation, "second, different message 222", callId: marker + "-2"),
            ErrorTestHost.FailingLogin(marker, FaultInjection.OtherSite, "third", marker + "-3"),
            ErrorTestHost.FailingLogin(marker, FaultInjection.ThirdSite, "fourth", marker + "-4"),
        })
        {
            using var response = await client.SendAsync(request);
            codes.Add(await ErrorContract.AssertUnexpectedAsync(response));
        }

        var rows = new List<ErrorLogRow>();
        foreach (var code in codes)
        {
            rows.Add(await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code));
        }

        Assert.All(rows, r => Assert.Matches(ErrorContract.FingerprintPattern, r.Fingerprint!));
        Assert.Equal(rows[0].Fingerprint, rows[1].Fingerprint);   // same type, same top application frame, other message and endpoint
        Assert.NotEqual(rows[0].Fingerprint, rows[2].Fingerprint); // other type and frame
        Assert.NotEqual(rows[0].Fingerprint, rows[3].Fingerprint);
        Assert.NotEqual(rows[2].Fingerprint, rows[3].Fingerprint);
        Assert.Equal(4, codes.Distinct().Count());
    }

    [Fact]
    public async Task The_fingerprint_is_the_documented_hash_of_the_exception_type_and_the_top_application_frame()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();

        using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker));

        var code = await ErrorContract.AssertUnexpectedAsync(response);
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
        // The first stack frame inside Conversia is the helper of the fault injection (this test assembly lives in Conversia.* too).
        var details = row.ExceptionDetails!;
        var frame = Regex.Match(details, @"at (Conversia\.[\w.+`<>]+?)\(").Groups[1].Value;
        Assert.Contains("FaultInjection", frame);
        var normalized = frame.Replace('+', '.');
        var accepted = new[] { ErrorContract.ExpectedFingerprint("System.InvalidOperationException", frame), ErrorContract.ExpectedFingerprint("System.InvalidOperationException", normalized) };
        Assert.Contains(row.Fingerprint, accepted);
    }

    // ---- logging ----

    [Fact]
    public async Task The_code_is_logged_with_the_original_exception_as_a_structured_property()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();

        using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, message: $"logged failure {marker}"));

        var code = await ErrorContract.AssertUnexpectedAsync(response);
        var entry = _host.Logs.Entries.FirstOrDefault(e =>
            e.Level == LogLevel.Error && e.Category.EndsWith(MiddlewareCategory, StringComparison.Ordinal) && e.HasPropertyValue(code));
        Assert.True(entry is not null, $"No Error log of {MiddlewareCategory} carries the code {code} as a property. Entries mentioning it: {string.Join(" | ", _host.Logs.Mentioning(code).Select(e => $"{e.Level} {e.Category}"))}");
        Assert.Contains($"logged failure {marker}", entry!.Exception);
        Assert.Contains("/api/auth/login", entry.FullText);
    }

    // ---- exactly one INSERT, no reads ----

    [Fact]
    public async Task Registering_one_failure_costs_exactly_one_database_command_an_insert()
    {
        // A host of its own on its own empty database, measured after its startup purge has finished: nothing else is talking to it.
        await using var own = await ErrorTestHost.StartOnOwnDatabaseAsync(_shared);
        var host = own.Host;
        var marker = ErrorTestHost.NewMarker();
        using var client = host.NewClient();
        using (await client.SendAsync(ErrorTestHost.FailingLogin(marker + "-warm", callId: marker + "-warm"))) // pays the cold start once
        {
        }

        var before = host.Commands.CommandsExceptRetentionPurge.Count;
        using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker));
        var during = host.Commands.CommandsExceptRetentionPurge.Skip(before).ToList();

        var code = await ErrorContract.AssertUnexpectedAsync(response);
        await ErrorLogDb.RequireByCodeAsync(host.ConnectionString, code);
        var command = Assert.Single(during);
        Assert.Contains("INSERT", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ErrorLogs", command);
    }

    // ---- known outcomes are never registered ----

    public static TheoryData<string> KnownOutcomes => new()
    {
        "duplicate-email-409", "duplicate-branch-409", "conversation-not-found-404", "user-not-found-404", "unknown-route-404",
        "validation-400", "malformed-json-400", "undefined-permission-400",
        "unauthenticated-401", "invalid-credentials-401", "forbidden-403", "login-rate-limited-429",
    };

    [Theory]
    [MemberData(nameof(KnownOutcomes))]
    public async Task A_known_outcome_creates_no_row_and_carries_no_code(string scenario)
    {
        var marker = ErrorTestHost.NewMarker();
        var (factory, run, expected) = Scenario(scenario, marker);

        using var response = await run();

        Assert.Equal(expected, response.StatusCode);
        await ErrorContract.AssertNotRegisteredAsync(response);

        // Positive control: the same host registers a real failure carrying the same marker, so "no row" cannot mean "nothing works".
        using var controlClient = factory.CreateClient();
        controlClient.DefaultRequestHeaders.Add(TestClientIp.HeaderName, TestClientIp.Next());
        using var control = await controlClient.SendAsync(ErrorTestHost.FailingLogin(marker, callId: marker + "-control"));
        var controlCode = await ErrorContract.AssertUnexpectedAsync(control);
        var rows = await ErrorLogDb.ByUserAgentAsync(_host.ConnectionString, marker);
        var only = Assert.Single(rows);
        Assert.Equal(controlCode, only.Code);
    }

    private (WebApplicationFactory<Program> Factory, Func<Task<HttpResponseMessage>> Run, HttpStatusCode Expected) Scenario(string name, string marker)
    {
        HttpClient Client(string? token = null)
        {
            var client = _host.NewClient(token, TestClientIp.Next());
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", marker);
            return client;
        }

        async Task<(HttpClient Client, TenantSession Session)> OwnerAsync()
        {
            using var anonymous = Client();
            var session = await RegisterTenantSessionAsync(anonymous);
            return (Client(session.OwnerToken), session);
        }

        var host = _host.Factory;
        return name switch
        {
            "duplicate-email-409" => (host, async () =>
            {
                var (_, session) = await OwnerAsync();
                using var client = Client();
                return await client.PostAsJsonAsync("/api/auth/register-tenant", new { tenantName = UniqueTenantName("dup"), email = session.OwnerEmail, password = StrongPassword });
            }, HttpStatusCode.Conflict),
            "duplicate-branch-409" => (host, async () =>
            {
                var (client, _) = await OwnerAsync();
                using var first = await client.PostAsJsonAsync("/api/branches", new { name = "Sede Centro" });
                Assert.Equal(HttpStatusCode.Created, first.StatusCode);
                return await client.PostAsJsonAsync("/api/branches", new { name = "Sede Centro" }); // the same name in the tenant: DuplicateBranchNameException
            }, HttpStatusCode.Conflict),
            "conversation-not-found-404" => (host, async () =>
            {
                var (client, _) = await OwnerAsync();
                return await client.GetAsync($"/api/conversations/{Guid.NewGuid()}");
            }, HttpStatusCode.NotFound),
            "user-not-found-404" => (host, async () =>
            {
                var (client, _) = await OwnerAsync();
                return await client.PostAsJsonAsync($"/api/users/{Guid.NewGuid()}/permissions", new { permission = (int)Permission.UsersView });
            }, HttpStatusCode.NotFound),
            "unknown-route-404" => (host, () => Client().GetAsync("/api/nothing-lives-here"), HttpStatusCode.NotFound),
            "validation-400" => (host, () => Client().PostAsJsonAsync("/api/auth/login", new { }), HttpStatusCode.BadRequest),
            "malformed-json-400" => (host, () => Client().PostAsync("/api/auth/login", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest),
            "undefined-permission-400" => (host, async () =>
            {
                var (client, session) = await OwnerAsync();
                return await RevokeAsync(client, session.OwnerId, "999");
            }, HttpStatusCode.BadRequest),
            "unauthenticated-401" => (host, () => Client().GetAsync("/api/users/me"), HttpStatusCode.Unauthorized),
            "invalid-credentials-401" => (host, () => Client().PostAsJsonAsync("/api/auth/login", new { email = UniqueEmail("nobody"), password = StrongPassword }), HttpStatusCode.Unauthorized),
            "forbidden-403" => (host, async () =>
            {
                var (_, session) = await OwnerAsync();
                using var anonymous = Client();
                var member = await SeedMemberAsync(_shared, anonymous, session.TenantId);
                return await Client(member.Token).GetAsync("/api/users");
            }, HttpStatusCode.Forbidden),
            "login-rate-limited-429" => LoginRateLimited(marker),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };
    }

    private (WebApplicationFactory<Program>, Func<Task<HttpResponseMessage>>, HttpStatusCode) LoginRateLimited(string marker)
    {
        var limited = _shared.GetOrAddHost("error-registry:main-login-limited", () => _host.Factory.WithWebHostBuilder(
            builder => builder.UseSetting("RateLimiting:Login:PermitLimit", "1")));
        return (limited, async () =>
        {
            using var client = limited.CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", marker);
            client.DefaultRequestHeaders.Add(TestClientIp.HeaderName, TestClientIp.Next());
            using (await client.PostAsJsonAsync("/api/auth/login", new { email = UniqueEmail("nobody"), password = StrongPassword }))
            {
            }

            return await client.PostAsJsonAsync("/api/auth/login", new { email = UniqueEmail("nobody"), password = StrongPassword });
        }, HttpStatusCode.TooManyRequests);
    }

    // ---- unexpected, but not the client's doing ----

    [Fact]
    public async Task A_cancellation_that_is_not_the_client_going_away_is_an_unexpected_error()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();

        using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, FaultInjection.CanceledNotAborted));

        var code = await ErrorContract.AssertUnexpectedAsync(response);
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
        Assert.Equal("System.OperationCanceledException", row.ExceptionType);
    }

    // ---- client-caused noise is not registered ----

    [Fact]
    public async Task A_request_the_client_abandoned_creates_no_row_and_no_500_body()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();
        using var cancellation = new CancellationTokenSource();
        var errorsBefore = CountOfMiddleware(LogLevel.Error);

        var sending = client.SendAsync(ErrorTestHost.FailingLogin(marker, FaultInjection.WaitForAbort), cancellation.Token);
        await _host.Signals.WaitStartedAsync(marker); // the request is inside the use case, waiting
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

        var recorded = await _host.Recorder.CompletedAsync(marker);
        Assert.Null(recorded.Escaped);
        Assert.Equal(string.Empty, recorded.Body);
        Assert.False(recorded.ErrorIdHeader, $"An abandoned request carries no {ErrorContract.HeaderName}.");
        Assert.Equal(errorsBefore, CountOfMiddleware(LogLevel.Error)); // nothing is reported as an error

        // Positive control on the same host and marker.
        using var control = await client.SendAsync(ErrorTestHost.FailingLogin(marker, callId: marker + "-control"));
        var controlCode = await ErrorContract.AssertUnexpectedAsync(control);
        Assert.Equal(controlCode, Assert.Single(await ErrorLogDb.ByUserAgentAsync(_host.ConnectionString, marker)).Code);
    }

    public static TheoryData<string, int> MalformedRequests => new()
    {
        { FaultInjection.BadRequest400, 400 },
        { FaultInjection.BadRequest408, 408 },
        { FaultInjection.BadRequest413, 413 },
    };

    [Theory]
    [MemberData(nameof(MalformedRequests))]
    public async Task A_malformed_or_oversized_request_answers_its_own_4xx_in_spanish_and_creates_no_row(string fault, int status)
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();
        var errorsBefore = CountOfMiddleware(LogLevel.Error);

        using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, fault));

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        var report = new ApiMessageReport();
        await report.CheckAsync(fault, response, (HttpStatusCode)status);
        report.AssertClean();
        var problem = await ReadProblemJsonAsync(response);
        Assert.Equal(status, problem.Status);
        await ErrorContract.AssertNotRegisteredAsync(response);
        Assert.Equal(errorsBefore, CountOfMiddleware(LogLevel.Error));

        using var control = await client.SendAsync(ErrorTestHost.FailingLogin(marker, callId: marker + "-control"));
        var controlCode = await ErrorContract.AssertUnexpectedAsync(control);
        Assert.Equal(controlCode, Assert.Single(await ErrorLogDb.ByUserAgentAsync(_host.ConnectionString, marker)).Code);
    }

    [Fact]
    public async Task A_request_whose_own_database_context_is_broken_is_still_registered_through_a_context_of_its_own()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();

        using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, FaultInjection.BrokenContext));

        var code = await ErrorContract.AssertUnexpectedAsync(response);
        var row = await ErrorLogDb.RequireByCodeAsync(_host.ConnectionString, code);
        Assert.Contains("DbUpdateException", row.ExceptionType);
        Assert.Contains("DbUpdateException", row.ExceptionDetails);
        Assert.Equal(marker, row.UserAgent);
    }

    // ---- the response was already on its way ----

    [Fact]
    public async Task When_the_response_has_already_started_the_error_is_still_registered_the_connection_is_aborted_and_no_body_is_appended()
    {
        var marker = ErrorTestHost.NewMarker();
        using var client = _host.NewClient();
        Exception? readFailure = null;

        try
        {
            using var response = await client.SendAsync(ErrorTestHost.FailingLogin(marker, FaultInjection.ResponseStarted), HttpCompletionOption.ResponseHeadersRead);
            _ = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            readFailure = ex;
        }

        var recorded = await _host.Recorder.CompletedAsync(marker);
        Assert.True(recorded.HasStarted, "The failure must have happened after the response started.");
        Assert.Equal("partial-body", recorded.Body); // nothing was appended to the bytes already sent
        Assert.Null(recorded.Escaped);

        // The abort is provable over TestServer in two independent ways: HttpContext.Abort cancelled RequestAborted by the time the
        // pipeline returned, and the client's read of the half-sent response fails instead of ending cleanly.
        Assert.True(recorded.Aborted, "The connection of a response that had already started must be aborted (HttpContext.Abort).");
        Assert.NotNull(readFailure);

        var row = Assert.Single(await ErrorLogDb.ByUserAgentAsync(_host.ConnectionString, marker));
        Assert.Equal("System.InvalidOperationException", row.ExceptionType);
        Assert.Equal("/api/auth/login", row.Path);
        Assert.Contains(_host.Logs.Entries, e => e.Level == LogLevel.Error && e.Mentions(row.Code));
    }

    private int CountOfMiddleware(LogLevel level)
        => _host.Logs.Entries.Count(e => e.Level == level && e.Category.EndsWith(MiddlewareCategory, StringComparison.Ordinal));

    private static void AssertServiceVersion(string? stored)
    {
        Assert.False(string.IsNullOrWhiteSpace(stored));
        Assert.True(stored!.Length <= 50);
        var attribute = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
            typeof(Program).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        var expected = (attribute?.InformationalVersion ?? typeof(Program).Assembly.GetName().Version!.ToString()).Split('+')[0];
        Assert.StartsWith(expected, stored);
    }
}
