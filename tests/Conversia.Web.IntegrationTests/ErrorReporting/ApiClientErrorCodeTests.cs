using System.Net;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Web.ErrorReporting;
using Conversia.Web.IntegrationTests.Infrastructure;
using Conversia.Web.Services;
using Microsoft.Extensions.Logging;

namespace Conversia.Web.IntegrationTests.ErrorReporting;

/// <summary>
/// Every outcome of the API client that the user experiences as "the service is not available" carries the code of that unexpected
/// error, so the pages can show it: the API's own code when it already registered the failure (and then the Web does not report it
/// again), a code of the Web's own — reported best effort through the <see cref="IWebErrorReporter"/> — when the API said nothing
/// usable or could not be reached. Known outcomes (validation, 401, 403, 404, 409, 429, success) carry no code and are never reported.
/// Driven through a fake backend and a recording reporter; no host, no database.
/// </summary>
public class ApiClientErrorCodeTests
{
    private const string Token = "token-of-the-caller";
    private static readonly Uri ApiBase = new(WebTestHost.ApiBaseUrl);

    private static string NewCode() => ErrorCode.New(DateTime.UtcNow);

    private sealed record Setup(ApiClient Client, FakeBackend Backend, RecordingReporter Reporter, CapturedLogs Logs);

    private static Setup Create(Func<BackendRequest, Task<HttpResponseMessage?>> data, bool withReporter = true)
    {
        var backend = new FakeBackend { Data = data };
        var reporter = new RecordingReporter();
        var logs = new CapturedLogs();
        var client = new ApiClient(new HttpClient(backend) { BaseAddress = ApiBase }, new CapturedLogger<ApiClient>(logs), withReporter ? reporter : null);
        return new Setup(client, backend, reporter, logs);
    }

    private static Setup Failing(string failure) => Create(FakeBackend.UpstreamFailure(failure));

    private static Func<BackendRequest, Task<HttpResponseMessage?>> Answer(HttpStatusCode status, string? body, string contentType = "application/problem+json", string? errorIdHeader = null)
        => _ => Task.FromResult<HttpResponseMessage?>(FakeBackend.Raw(status, body, contentType, errorIdHeader: errorIdHeader));

    /// <summary>One representative call per kind of request: reads, writes, the unauthenticated ones and the ones with a body or a query.</summary>
    public static TheoryData<string> Calls => new() { "login", "register", "profile", "users", "createUser", "grant", "revoke", "conversations", "conversation", "assignAdvisor", "createBranch" };

    private static async Task<IApiOutcome> CallAsync(IApiClient client, string call) => call switch
    {
        "login" => await client.LoginAsync(new LoginRequest { Email = "a@example.test", Password = "Sup3r-Secret!" }),
        "register" => await client.RegisterTenantAsync(new RegisterTenantRequest { TenantName = "Acme", Email = "a@example.test", Password = "Sup3r-Secret!" }),
        "profile" => await client.GetMyProfileAsync(Token),
        "users" => await client.GetUsersAsync(Token),
        "createUser" => await client.CreateUserAsync(Token, new CreateUserRequest { Email = "n@example.test", Password = "Sup3r-Secret!" }),
        "grant" => await client.AssignPermissionAsync(Token, Guid.NewGuid(), Permission.UsersView),
        "revoke" => await client.RevokePermissionAsync(Token, Guid.NewGuid(), Permission.UsersView),
        "conversations" => await client.GetConversationsAsync(Token, new ConversationQuery { Page = 1, PageSize = 10 }),
        "conversation" => await client.GetConversationAsync(Token, Guid.NewGuid()),
        "assignAdvisor" => await client.AssignAdvisorAsync(Token, Guid.NewGuid(), new AssignAdvisorRequest { AdvisorId = Guid.NewGuid() }),
        "createBranch" => await client.CreateBranchAsync(Token, new CreateBranchRequest { Name = "Centro" }),
        _ => throw new ArgumentOutOfRangeException(nameof(call), call, null),
    };

    // ---- the API already registered the failure: its code is carried, nothing is reported again ----

    public static TheoryData<string> CodeCarriers => new() { "body", "header", "body and header" };

    private static HttpResponseMessage WithCode(string carrier, string code)
    {
        var body = $$"""{"title":"Ocurrió un error inesperado","status":500,"errorId":"{{code}}"}""";
        return carrier switch
        {
            "body" => FakeBackend.Raw(HttpStatusCode.InternalServerError, body),
            "header" => FakeBackend.Raw(HttpStatusCode.InternalServerError, """{"title":"Ocurrió un error inesperado","status":500}""", errorIdHeader: code),
            _ => FakeBackend.UnexpectedWithCode(code),
        };
    }

    [Theory]
    [MemberData(nameof(CodeCarriers))]
    public async Task A_500_that_carries_the_apis_code_is_unavailable_with_that_code_and_is_not_reported_again(string carrier)
    {
        var code = NewCode();
        var setup = Create(_ => Task.FromResult<HttpResponseMessage?>(WithCode(carrier, code)));

        var result = await setup.Client.GetUsersAsync(Token);

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        Assert.Equal(code, result.ErrorCode);
        Assert.Empty(setup.Reporter.Reports);
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Every_call_carries_the_apis_code_of_a_500(string call)
    {
        var code = NewCode();
        var setup = Create(_ => Task.FromResult<HttpResponseMessage?>(FakeBackend.UnexpectedWithCode(code)));

        var outcome = await CallAsync(setup.Client, call);

        Assert.True(outcome.Status == ApiResultStatus.Unavailable, $"{call}: expected Unavailable but got {outcome.Status}.");
        Assert.True(outcome.ErrorCode == code, $"{call}: expected the API's code {code} but got '{outcome.ErrorCode}'.");
        Assert.Empty(setup.Reporter.Reports);
    }

    [Fact]
    public async Task A_500_that_carries_the_apis_header_is_trusted_whatever_its_body()
    {
        var code = NewCode();
        var setup = Create(Answer(HttpStatusCode.InternalServerError, "<html>an error page</html>", "text/html", errorIdHeader: code));

        var result = await setup.Client.GetMyProfileAsync(Token);

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        Assert.Equal(code, result.ErrorCode);
        Assert.Empty(setup.Reporter.Reports);
    }

    [Fact]
    public async Task The_apis_code_is_logged_without_reporting_the_failure_again()
    {
        var code = NewCode();
        var setup = Create(_ => Task.FromResult<HttpResponseMessage?>(FakeBackend.UnexpectedWithCode(code)));

        await setup.Client.GetUsersAsync(Token);

        Assert.Contains(setup.Logs.Mentioning(code), record => record.Level >= LogLevel.Information);
    }

    // ---- an "id" the Web cannot trust is not shown: the Web's own code takes its place ----

    public static TheoryData<string> UntrustworthyIds => new()
    {
        "<script>alert(1)</script>",
        "ERR-1",
        "hello",
        "err-20261018-7k3m9q",
        "ERR-20261018-7K3M9QX",
        "ERR-20261018-7K3M9I",
        "ERR-2026101A-7K3M9Q",
        "ERR-20261018-7K3M9Q extra",
        "",
        "   ",
    };

    [Theory]
    [MemberData(nameof(UntrustworthyIds))]
    public async Task An_error_id_that_is_not_a_well_formed_code_is_never_carried(string untrustworthy)
    {
        var setup = Create(_ => Task.FromResult<HttpResponseMessage?>(FakeBackend.Raw(
            HttpStatusCode.InternalServerError,
            System.Text.Json.JsonSerializer.Serialize(new { title = "x", status = 500, errorId = untrustworthy }),
            errorIdHeader: untrustworthy.Contains('<') || untrustworthy.Length == 0 ? null : untrustworthy.Trim())));

        var result = await setup.Client.GetUsersAsync(Token);

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        var report = Assert.Single(setup.Reporter.Reports);
        Assert.Equal(setup.Reporter.CodeAnsweredFor(report), result.ErrorCode);
        Assert.NotEqual(untrustworthy, result.ErrorCode);
    }

    [Fact]
    public async Task An_error_id_of_a_non_string_type_is_not_carried()
    {
        var setup = Create(Answer(HttpStatusCode.InternalServerError, """{"title":"x","status":500,"errorId":{"code":"ERR-20261018-7K3M9Q"}}"""));

        var result = await setup.Client.GetUsersAsync(Token);

        var report = Assert.Single(setup.Reporter.Reports);
        Assert.Equal(setup.Reporter.CodeAnsweredFor(report), result.ErrorCode);
    }

    // ---- the API said nothing usable: the Web reports it with a code of its own ----

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, ErrorKind.UpstreamUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErrorKind.UpstreamUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, ErrorKind.UpstreamUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorKind.UpstreamBadResponse)]
    public async Task A_5xx_without_a_code_is_reported_and_carries_the_code_the_reporter_gave(HttpStatusCode status, ErrorKind expectedKind)
    {
        var setup = Create(Answer(status, """{"title":"An unexpected error occurred.","status":500}"""));

        var result = await setup.Client.GetUsersAsync(Token);

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        var report = Assert.Single(setup.Reporter.Reports);
        Assert.Equal(expectedKind, report.Kind);
        Assert.Equal((int)status, report.StatusCode);
        ErrorCodeAssertions.AssertWellFormed(result.ErrorCode);
        Assert.Equal(setup.Reporter.CodeAnsweredFor(report), result.ErrorCode);
    }

    public static TheoryData<string> TransportFailures => new() { "connection refused", "timeout" };

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task A_network_failure_or_timeout_is_an_UpstreamUnavailable_report_with_the_exception_and_a_code(string failure)
    {
        var setup = Failing(failure);

        var result = await setup.Client.GetUsersAsync(Token);

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        var report = Assert.Single(setup.Reporter.Reports);
        Assert.Equal(ErrorKind.UpstreamUnavailable, report.Kind);
        Assert.NotNull(report.Exception);
        Assert.Equal(setup.Reporter.CodeAnsweredFor(report), result.ErrorCode);
        ErrorCodeAssertions.AssertWellFormed(result.ErrorCode);
    }

    [Fact]
    public async Task A_real_http_timeout_is_an_UpstreamUnavailable_report_too()
    {
        var backend = new FakeBackend { Data = request => FakeBackend.HangAsync(request.Cancellation)! };
        var reporter = new RecordingReporter();
        var client = new ApiClient(new HttpClient(backend) { BaseAddress = ApiBase, Timeout = TimeSpan.FromMilliseconds(200) }, null, reporter);

        var result = await client.GetUsersAsync(Token);

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        var report = Assert.Single(reporter.Reports);
        Assert.Equal(ErrorKind.UpstreamUnavailable, report.Kind);
        Assert.Equal(reporter.CodeAnsweredFor(report), result.ErrorCode);
    }

    public static TheoryData<string> MalformedAnswers => new() { "200 html", "200 empty", "200 truncated json" };

    [Theory]
    [MemberData(nameof(MalformedAnswers))]
    public async Task An_answer_the_web_cannot_make_sense_of_is_an_UpstreamBadResponse_report_with_a_code(string failure)
    {
        var setup = Failing(failure);

        var result = await setup.Client.GetConversationsAsync(Token, new ConversationQuery { Page = 1, PageSize = 10 });

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        var report = Assert.Single(setup.Reporter.Reports);
        Assert.Equal(ErrorKind.UpstreamBadResponse, report.Kind);
        Assert.Equal(setup.Reporter.CodeAnsweredFor(report), result.ErrorCode);
    }

    [Fact]
    public async Task A_usable_status_with_an_unusable_body_is_reported_as_a_bad_response()
    {
        var setup = Create(Answer(HttpStatusCode.OK, """{"items":null,"page":1}""", "application/json"));

        var result = await setup.Client.GetConversationsAsync(Token, new ConversationQuery { Page = 1, PageSize = 10 });

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        Assert.Equal(ErrorKind.UpstreamBadResponse, Assert.Single(setup.Reporter.Reports).Kind);
        ErrorCodeAssertions.AssertWellFormed(result.ErrorCode);
    }

    // The kind of an unusual answer is for the implementation to choose; what is required is that it is an error with a code.
    [Fact]
    public async Task A_status_the_api_never_answers_with_is_reported_and_carries_a_code()
    {
        var setup = Create(Answer((HttpStatusCode)418, """{"title":"x","status":418}"""));

        AssertReportedWithItsCode(setup, await setup.Client.GetUsersAsync(Token));
    }

    [Fact]
    public async Task A_400_that_is_not_a_validation_problem_is_reported_and_carries_a_code()
    {
        var setup = Create(Answer(HttpStatusCode.BadRequest, "<html>bad</html>", "text/html"));

        AssertReportedWithItsCode(setup, await setup.Client.LoginAsync(new LoginRequest { Email = "a@example.test", Password = "p" }));
    }

    private static void AssertReportedWithItsCode(Setup setup, IApiOutcome result)
    {
        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        var report = Assert.Single(setup.Reporter.Reports);
        Assert.Equal(setup.Reporter.CodeAnsweredFor(report), result.ErrorCode);
        ErrorCodeAssertions.AssertWellFormed(result.ErrorCode);
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Every_call_carries_a_code_when_the_api_is_down(string call)
    {
        var setup = Failing("connection refused");

        var outcome = await CallAsync(setup.Client, call);

        Assert.True(outcome.Status == ApiResultStatus.Unavailable, $"{call}: expected Unavailable but got {outcome.Status}.");
        var report = Assert.Single(setup.Reporter.Reports);
        Assert.True(outcome.ErrorCode == setup.Reporter.CodeAnsweredFor(report), $"{call}: the outcome carries '{outcome.ErrorCode}', not the code of its report.");
    }

    // ---- what the report says about the call (and what it must not) ----

    [Fact]
    public async Task The_report_names_the_upstream_call_but_not_its_query_nor_the_token()
    {
        var setup = Failing("503 empty");

        var privateBranch = Guid.NewGuid();
        await setup.Client.GetConversationsAsync("secret-bearer-token-of-ana", new ConversationQuery { Page = 2, PageSize = 10, BranchId = privateBranch });

        var report = Assert.Single(setup.Reporter.Reports);
        Assert.Equal("GET", report.UpstreamMethod);
        Assert.Contains("api/conversations", report.UpstreamPath);
        Assert.DoesNotContain("?", report.UpstreamPath);
        Assert.DoesNotContain(privateBranch.ToString(), report.UpstreamPath);
        Assert.DoesNotContain("secret-bearer-token-of-ana", report.UpstreamPath);
        Assert.DoesNotContain("secret-bearer-token-of-ana", report.Exception?.ToString() ?? string.Empty);
    }

    // ---- known outcomes are not errors ----

    public static TheoryData<int, ApiResultStatus> KnownOutcomes => new()
    {
        { 401, ApiResultStatus.Unauthorized },
        { 403, ApiResultStatus.Forbidden },
        { 404, ApiResultStatus.NotFound },
        { 409, ApiResultStatus.Conflict },
        { 429, ApiResultStatus.RateLimited },
    };

    [Theory]
    [MemberData(nameof(KnownOutcomes))]
    public async Task Known_outcomes_carry_no_code_and_are_not_reported(int status, ApiResultStatus expected)
    {
        var setup = Create(Answer((HttpStatusCode)status, """{"title":"x","status":0}"""));

        var result = await setup.Client.GetUsersAsync(Token);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Empty(setup.Reporter.Reports);
    }

    [Fact]
    public async Task A_4xx_that_happens_to_carry_an_error_id_is_still_a_known_outcome()
    {
        var setup = Create(Answer(HttpStatusCode.NotFound, $$"""{"title":"x","status":404,"errorId":"{{NewCode()}}"}"""));

        var result = await setup.Client.GetConversationAsync(Token, Guid.NewGuid());

        Assert.Equal(ApiResultStatus.NotFound, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Empty(setup.Reporter.Reports);
    }

    [Fact]
    public async Task A_validation_problem_carries_no_code()
    {
        var setup = Create(Answer(HttpStatusCode.BadRequest, """{"title":"x","status":400,"errors":{"Email":["Correo no válido."]}}"""));

        var result = await setup.Client.LoginAsync(new LoginRequest { Email = "x", Password = "p" });

        Assert.Equal(ApiResultStatus.ValidationFailed, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Empty(setup.Reporter.Reports);
    }

    [Fact]
    public async Task Success_carries_no_code_and_is_not_reported()
    {
        var setup = Create(_ => Task.FromResult<HttpResponseMessage?>(null));

        var result = await setup.Client.GetMyProfileAsync(Token);

        Assert.True(result.IsSuccess);
        Assert.Null(result.ErrorCode);
        Assert.Empty(setup.Reporter.Reports);
    }

    [Fact]
    public async Task A_caller_that_gives_up_gets_its_cancellation_and_nothing_is_reported()
    {
        var setup = Create(request => FakeBackend.HangAsync(request.Cancellation)!);
        using var cancellation = new CancellationTokenSource();
        var pending = setup.Client.GetUsersAsync(Token, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeouts.Signal));

        Assert.Empty(setup.Reporter.Reports);
    }

    // ---- the user always gets a code ----

    [Fact]
    public async Task A_reporter_that_fails_cannot_take_the_code_away()
    {
        var setup = Failing("connection refused");
        setup.Reporter.Failure = _ => new InvalidOperationException("the reporter is broken");

        var result = await setup.Client.GetUsersAsync(Token);

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        ErrorCodeAssertions.AssertWellFormed(result.ErrorCode);
    }

    [Fact]
    public async Task Without_a_reporter_the_client_still_gives_a_code()
    {
        var setup = Create(FakeBackend.UpstreamFailure("connection refused"), withReporter: false);

        var result = await setup.Client.GetUsersAsync(Token);

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        ErrorCodeAssertions.AssertWellFormed(result.ErrorCode);
    }

    [Theory]
    [MemberData(nameof(MalformedAnswers))]
    public async Task Without_a_reporter_every_unavailable_outcome_has_a_code(string failure)
    {
        var setup = Create(FakeBackend.UpstreamFailure(failure), withReporter: false);

        var result = await setup.Client.GetConversationsAsync(Token, new ConversationQuery { Page = 1, PageSize = 10 });

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        ErrorCodeAssertions.AssertWellFormed(result.ErrorCode);
    }

    // ---- many callers at once share nothing ----

    [Fact]
    public async Task Parallel_failures_on_one_client_each_get_their_own_report_and_code()
    {
        const int calls = 100;
        var setup = Failing("503 empty");
        var ids = Enumerable.Range(0, calls).Select(_ => Guid.NewGuid()).ToArray();

        var results = await Task.WhenAll(ids.Select(id => Task.Run(() => setup.Client.GetConversationAsync(Token, id)))).WaitAsync(TestTimeouts.Operation);

        Assert.Equal(calls, results.Select(r => r.ErrorCode).Distinct().Count());
        Assert.Equal(calls, setup.Reporter.Reports.Count);
        for (var i = 0; i < calls; i++)
        {
            var report = Assert.Single(setup.Reporter.Reports, r => r.UpstreamPath!.EndsWith(ids[i].ToString(), StringComparison.OrdinalIgnoreCase));
            Assert.Equal(setup.Reporter.CodeAnsweredFor(report), results[i].ErrorCode);
        }
    }

    [Fact]
    public async Task Parallel_failures_that_carry_the_apis_codes_each_keep_their_own()
    {
        const int calls = 100;
        var codes = new System.Collections.Concurrent.ConcurrentDictionary<Guid, string>();
        var setup = Create(request =>
        {
            var id = Guid.Parse(request.Path[(request.Path.LastIndexOf('/') + 1)..]);
            return Task.FromResult<HttpResponseMessage?>(FakeBackend.UnexpectedWithCode(codes.GetOrAdd(id, _ => NewCode())));
        });
        var ids = Enumerable.Range(0, calls).Select(_ => Guid.NewGuid()).ToArray();

        var results = await Task.WhenAll(ids.Select(id => Task.Run(() => setup.Client.GetConversationAsync(Token, id)))).WaitAsync(TestTimeouts.Operation);

        for (var i = 0; i < calls; i++)
        {
            Assert.Equal(codes[ids[i]], results[i].ErrorCode);
        }

        Assert.Empty(setup.Reporter.Reports);
    }
}
