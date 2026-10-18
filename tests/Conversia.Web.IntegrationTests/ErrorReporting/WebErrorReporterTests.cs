using System.Diagnostics;
using System.Text.Json;
using Conversia.Application.DTOs.Diagnostics;
using Conversia.Domain.Enums;
using Conversia.TestSupport;
using Conversia.Web.ErrorReporting;
using Conversia.Web.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Conversia.Web.IntegrationTests.ErrorReporting;

/// <summary>
/// The Web's reporter on its own: it turns one unexpected failure into a code, tells the API about it (best effort) and hands the
/// code back. It always answers with a well-formed code, never throws, never waits longer than <c>ErrorReporting:TimeoutSeconds</c>,
/// and falls back to the log, with the same code, when the API cannot take the report. What it sends is what the API's report endpoint
/// accepts, sanitized, and never a cookie, a token, a form or the shared key. Driven through a fake backend; no host.
/// </summary>
public class WebErrorReporterTests
{
    private static ReporterHarness Harness(params (string Key, string? Value)[] settings) => ReporterHarness.Create(settings);

    private static Task<string> ReportAsync(ReporterHarness harness, Exception? exception = null, Microsoft.AspNetCore.Http.HttpContext? context = null, ErrorKind kind = ErrorKind.Unhandled, CancellationToken cancellationToken = default)
        => harness.Reporter.ReportAsync(new WebErrorReport(kind, exception ?? ReporterHarness.Thrown(), context ?? ReporterHarness.Request()), cancellationToken);

    private static Dictionary<string, JsonElement> Fields(BackendRequest request)
    {
        using var document = request.BodyJson();
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static string? Text(Dictionary<string, JsonElement> fields, string name)
        => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ---- what is sent, and to whom ----

    [Fact]
    public async Task A_report_is_one_authenticated_json_post_to_the_apis_error_endpoint()
    {
        using var harness = Harness();

        var code = await ReportAsync(harness);

        var request = Assert.Single(harness.Backend.ReportRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://api.test/api/diagnostics/errors", request.Uri.ToString());
        Assert.Equal(ReporterHarness.Key, request.Header("X-Error-Report-Key"));
        Assert.Equal("application/json", request.Header("Content-Type")?.Split(';')[0]);
        Assert.Equal(code, request.ReportBody.Code);
        Assert.Equal("Unhandled", request.ReportBody.Kind);
        ErrorCodeAssertions.AssertWellFormed(code);
    }

    [Fact]
    public async Task The_report_carries_neither_cookies_nor_a_bearer_token_and_the_key_travels_only_in_its_header()
    {
        using var harness = Harness();
        var context = ReporterHarness.Request();
        context.Request.Headers.Cookie = ".Conversia.Auth=COOKIE-SECRET-123; theme=dark";
        context.Request.Headers.Authorization = "Bearer HEADER-SECRET.abc.def";

        await ReportAsync(harness, context: context);

        var request = Assert.Single(harness.Backend.ReportRequests);
        Assert.Null(request.Header("Cookie"));
        Assert.Null(request.Header("Authorization"));
        Assert.DoesNotContain(request.Headers, h => h.Value.Contains("COOKIE-SECRET-123") || h.Value.Contains("HEADER-SECRET"));
        Assert.DoesNotContain(ReporterHarness.Key, request.Body);
        Assert.Single(request.Headers, h => h.Value.Contains(ReporterHarness.Key));
    }

    [Fact]
    public async Task What_a_person_sent_in_cookies_headers_or_a_form_never_reaches_the_report()
    {
        using var harness = Harness();
        var context = ReporterHarness.Request("POST", "/login");
        context.Request.Headers.Cookie = ".Conversia.Auth=COOKIE-SECRET-123";
        context.Request.Headers.Authorization = "Bearer HEADER-SECRET.abc.def";
        context.Request.Headers["X-Custom-Secret"] = "CUSTOM-HEADER-SECRET";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Input.Email=ana%40example.test&Input.Password=FormSecret-999"));

        await ReportAsync(harness, context: context);

        var body = Assert.Single(harness.Backend.ReportRequests).Body!;
        foreach (var secret in new[] { "COOKIE-SECRET-123", "HEADER-SECRET", "CUSTOM-HEADER-SECRET", "FormSecret-999", "ana@example.test", "ana%40example.test" })
        {
            Assert.DoesNotContain(secret, body);
        }
    }

    [Fact]
    public async Task The_report_describes_the_exception_the_request_and_the_service()
    {
        using var harness = Harness();
        using var activity = new Activity("a request").Start();
        var exception = ReporterHarness.Thrown("kaput", new IOException("the inner problem"));
        var context = ReporterHarness.Request("POST", "/users", "?page=2&search=ana", "Mozilla/5.0 (tests)", "request-123");

        await ReportAsync(harness, exception, context);

        var body = Assert.Single(harness.Backend.ReportRequests).ReportBody;
        Assert.Equal("Unhandled", body.Kind);
        Assert.Equal("POST", body.HttpMethod);
        Assert.Equal("/users", body.Path);
        Assert.Contains("page=2", body.Query);
        Assert.Contains("search=ana", body.Query);
        Assert.Equal("Mozilla/5.0 (tests)", body.UserAgent);
        Assert.Equal("request-123", body.RequestId);
        Assert.Equal(activity.TraceId.ToHexString(), body.TraceId);
        Assert.Equal(500, body.StatusCode);
        Assert.Equal("System.InvalidOperationException", body.ExceptionType);
        Assert.Equal("kaput", body.ExceptionMessage);
        Assert.Contains("InvalidOperationException: kaput", body.ExceptionDetails);
        Assert.Contains("the inner problem", body.ExceptionDetails);
        Assert.Contains(nameof(ReporterHarness.ThrowInvalidOperation), body.ExceptionDetails);
        Assert.Equal(ReporterHarness.EnvironmentName, body.Environment);
        Assert.False(string.IsNullOrWhiteSpace(body.ServiceVersion));
        Assert.DoesNotContain('+', body.ServiceVersion!);
        Assert.False(string.IsNullOrWhiteSpace(body.Instance));
        ReportContract.AssertValidForTheApi(body);
    }

    [Theory]
    [InlineData(ErrorKind.UpstreamUnavailable, "UpstreamUnavailable")]
    [InlineData(ErrorKind.UpstreamBadResponse, "UpstreamBadResponse")]
    public async Task The_kind_is_reported_by_name_and_the_upstream_call_travels_in_the_context(ErrorKind kind, string expected)
    {
        using var harness = Harness();
        var report = new WebErrorReport(kind, new HttpRequestException("connection refused"), ReporterHarness.Request("GET", "/products"), 503, "GET", "api/products");

        await harness.Reporter.ReportAsync(report);

        var body = Assert.Single(harness.Backend.ReportRequests).ReportBody;
        Assert.Equal(expected, body.Kind);
        Assert.Equal(503, body.StatusCode);
        Assert.Equal("/products", body.Path);
        Assert.Contains("api/products", body.Context);
        Assert.Contains("GET", body.Context);
        using var context = JsonDocument.Parse(body.Context!);
        Assert.Equal(JsonValueKind.Object, context.RootElement.ValueKind);
        ReportContract.AssertValidForTheApi(body);
    }

    [Theory]
    [InlineData(ErrorKind.Unhandled)]
    [InlineData(ErrorKind.UpstreamUnavailable)]
    [InlineData(ErrorKind.UpstreamBadResponse)]
    public async Task A_failure_with_neither_a_request_nor_an_exception_is_still_a_report_the_api_accepts(ErrorKind kind)
    {
        using var harness = Harness();

        var code = await harness.Reporter.ReportAsync(new WebErrorReport(kind, UpstreamMethod: "GET", UpstreamPath: "api/users"));

        var body = Assert.Single(harness.Backend.ReportRequests).ReportBody;
        Assert.Equal(code, body.Code);
        Assert.False(string.IsNullOrWhiteSpace(body.Path), "The API requires a path.");
        Assert.False(string.IsNullOrWhiteSpace(body.ExceptionType), "The API requires an exception type.");
        ReportContract.AssertValidForTheApi(body);
    }

    // ---- sanitization, before anything leaves the Web ----

    [Theory]
    [MemberData(nameof(ReportContract.SecretsInMessages), MemberType = typeof(ReportContract))]
    public async Task Secrets_in_an_exception_are_redacted_before_the_report_is_sent(string message, string secret)
    {
        using var harness = Harness();
        var exception = ReporterHarness.Thrown(message, new InvalidOperationException("inner: " + message));

        await ReportAsync(harness, exception);

        var request = Assert.Single(harness.Backend.ReportRequests);
        Assert.DoesNotContain(secret, request.Body);
        ReportContract.AssertRedactionMarked(request.ReportBody);
    }

    [Fact]
    public async Task Secrets_in_the_query_string_are_redacted_and_the_rest_is_kept()
    {
        using var harness = Harness();
        var context = ReporterHarness.Request("GET", "/products", "?page=2&token=SECRET-TOKEN-VALUE&search=ana&password=HUNTER2&apiKey=KEY-VALUE-123");

        await ReportAsync(harness, context: context);

        var request = Assert.Single(harness.Backend.ReportRequests);
        foreach (var secret in new[] { "SECRET-TOKEN-VALUE", "HUNTER2", "KEY-VALUE-123" })
        {
            Assert.DoesNotContain(secret, request.Body);
        }

        Assert.Contains("page=2", request.ReportBody.Query);
        Assert.Contains("search=ana", request.ReportBody.Query);
    }

    // ---- the contract's limits ----

    [Fact]
    public async Task Enormous_inputs_are_cut_to_the_limits_the_api_accepts()
    {
        using var harness = Harness();
        var inner = new InvalidOperationException(new string('i', 30_000));
        var exception = ReporterHarness.Thrown(new string('m', 50_000), inner);
        var context = ReporterHarness.Request("POST", "/" + new string('p', 2_000), "?" + string.Join("&", Enumerable.Range(0, 800).Select(i => $"k{i}=v{i}")), new string('u', 2_000), new string('r', 500));

        await ReportAsync(harness, exception, context);

        var request = Assert.Single(harness.Backend.ReportRequests);
        var fields = Fields(request);
        foreach (var (field, limit) in ErrorLimits.RefusedWhenLongerReportFields)
        {
            if (Text(fields, field) is { } value)
            {
                Assert.True(value.Length <= limit, $"'{field}' has {value.Length} characters; the API refuses more than {limit}.");
            }
        }

        Assert.True(Text(fields, "exceptionDetails")!.Length <= ErrorLimits.ExceptionDetailsCap, "exceptionDetails is longer than the 16 000 characters the API keeps.");
        Assert.True(Text(fields, "code")!.Length <= 24);
        Assert.StartsWith("/", Text(fields, "path"));
        ReportContract.AssertValidForTheApi(request.ReportBody);
    }

    // ---- reporting switched off, or no key: log only, with the code ----

    public static TheoryData<string, string, string?> NotReporting => new()
    {
        { "no key", "ErrorReporting:ApiKey", null },
        { "blank key", "ErrorReporting:ApiKey", "" },
        { "whitespace key", "ErrorReporting:ApiKey", "   " },
        { "disabled", "ErrorReporting:Enabled", "false" },
    };

    [Theory]
    [MemberData(nameof(NotReporting))]
    public async Task Without_a_key_or_when_disabled_nothing_is_sent_and_the_code_is_logged_with_the_failure(string scenario, string setting, string? value)
    {
        using var harness = Harness((setting, value));
        var exception = ReporterHarness.Thrown("failure that stays in the log");

        var code = await ReportAsync(harness, exception);

        ErrorCodeAssertions.AssertWellFormed(code);
        Assert.True(harness.Backend.Requests.Count == 0, $"{scenario}: the Web called the API ({harness.Backend.Requests.Count} requests).");
        var logged = harness.Logs.Mentioning(code).Where(r => r.Level == LogLevel.Error).ToList();
        Assert.True(logged.Any(r => r.Mentions("failure that stays in the log")), $"{scenario}: no Error log carries the code {code} together with the failure.");
        Assert.DoesNotContain(harness.Logs.Records, r => r.Mentions(ReporterHarness.Key));
    }

    [Theory]
    [InlineData(ErrorKind.Unhandled)]
    [InlineData(ErrorKind.UpstreamUnavailable)]
    [InlineData(ErrorKind.UpstreamBadResponse)]
    public async Task Every_kind_of_failure_is_logged_with_its_code_when_nothing_can_be_sent(ErrorKind kind)
    {
        using var harness = Harness(("ErrorReporting:ApiKey", null));

        var code = await harness.Reporter.ReportAsync(new WebErrorReport(kind, new HttpRequestException("upstream trouble"), ReporterHarness.Request(), 503, "GET", "api/users"));

        Assert.Contains(harness.Logs.Mentioning(code), r => r.Level == LogLevel.Error);
    }

    // ---- the API cannot take the report: the user still has a code, the error is not masked ----

    [Theory]
    [MemberData(nameof(FakeBackend.ReportFailures), MemberType = typeof(FakeBackend))]
    public async Task When_the_api_cannot_take_the_report_the_reporter_still_answers_with_a_code_and_logs_it(string failure)
    {
        using var harness = Harness(("ErrorReporting:TimeoutSeconds", "1"));
        harness.Backend.Reports = FakeBackend.ReportFailure(failure);
        var exception = ReporterHarness.Thrown("the original failure, unmasked");

        var code = await ReportAsync(harness, exception).WaitAsync(TestTimeouts.Operation);

        ErrorCodeAssertions.AssertWellFormed(code);
        Assert.True(harness.Backend.ReportRequests.Count == 1, $"{failure}: expected exactly one attempt (no retries, no report about the failed report) but saw {harness.Backend.ReportRequests.Count}.");
        if (FakeBackend.ReachedTheApi(failure))
        {
            // The API took the report, only its answer is unusable: the code only has to be findable in the log.
            Assert.Contains(harness.Logs.Mentioning(code), r => r.Level >= LogLevel.Information);
        }
        else
        {
            // The report did not get through: the log is the only place left to find the failure, with the code.
            var logged = harness.Logs.Mentioning(code).Where(r => r.Level == LogLevel.Error).ToList();
            Assert.True(logged.Any(r => r.Mentions("the original failure, unmasked")), $"{failure}: no Error log carries the code {code} together with the original failure.");
        }

        Assert.DoesNotContain(harness.Logs.Records, r => r.Mentions(ReporterHarness.Key));
        Assert.DoesNotContain(harness.Logs.Entries, entry => entry.Contains(ReporterHarness.Key));
    }

    /// <summary>Makes the report endpoint hang, reports, and returns how long the Web waited before it cancelled the hung request.</summary>
    private static async Task<TimeSpan> WaitForAHungApiAsync(ReporterHarness harness)
    {
        var startedAt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledAt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Backend.Reports = request =>
        {
            startedAt.TrySetResult(Stopwatch.GetTimestamp());
            request.Cancellation.Register(() => cancelledAt.TrySetResult(Stopwatch.GetTimestamp()));
            return FakeBackend.HangAsync(request.Cancellation);
        };

        var code = await ReportAsync(harness).WaitAsync(TestTimeouts.Operation);

        ErrorCodeAssertions.AssertWellFormed(code);
        var started = await startedAt.Task.WaitAsync(TestTimeouts.Signal);
        var cancelled = await cancelledAt.Task.WaitAsync(TestTimeouts.Signal);
        return Stopwatch.GetElapsedTime(started, cancelled);
    }

    [Fact]
    public async Task A_hung_api_is_given_up_on_after_the_configured_timeout_and_the_request_is_cancelled()
    {
        using var harness = Harness(("ErrorReporting:TimeoutSeconds", "1"));

        var waited = await WaitForAHungApiAsync(harness);

        Assert.InRange(waited, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(1) + TestTimeouts.Slack);
    }

    [Fact]
    public async Task Without_a_setting_the_wait_for_a_hung_api_is_two_seconds()
    {
        using var harness = Harness();

        var waited = await WaitForAHungApiAsync(harness);

        Assert.InRange(waited, TimeSpan.FromSeconds(1.9), TimeSpan.FromSeconds(2) + TestTimeouts.Slack);
    }

    [Fact]
    public async Task A_caller_that_has_already_given_up_still_gets_a_code_and_no_exception()
    {
        using var harness = Harness();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var failure = await Record.ExceptionAsync(async () =>
        {
            var code = await ReportAsync(harness, cancellationToken: cancelled.Token);
            ErrorCodeAssertions.AssertWellFormed(code);
        });

        Assert.Null(failure);
    }

    [Fact]
    public async Task A_report_with_a_broken_exception_still_gets_a_code()
    {
        using var harness = Harness();

        var code = await ReportAsync(harness, new BrokenException());

        ErrorCodeAssertions.AssertWellFormed(code);
        Assert.Contains(harness.Logs.Mentioning(code), r => r.Level >= LogLevel.Information);
    }

    private sealed class BrokenException : Exception
    {
        public override string Message => throw new InvalidOperationException("Message is broken");

        public override string ToString() => throw new InvalidOperationException("ToString is broken");
    }

    // ---- the API's answer ----

    [Fact]
    public async Task The_code_the_api_stored_is_the_code_the_user_gets_when_it_replaced_ours()
    {
        using var harness = Harness();
        var replacement = Domain.Common.ErrorCode.New(DateTime.UtcNow);
        harness.Backend.Reports = _ => Task.FromResult(FakeBackend.Answer(System.Net.HttpStatusCode.Created, new ReportErrorResponse(replacement, true)));

        var code = await ReportAsync(harness);

        Assert.Equal(replacement, code);
        Assert.NotEqual(replacement, Assert.Single(harness.Backend.ReportRequests).ReportBody.Code);
    }

    [Fact]
    public async Task A_report_the_api_did_not_persist_still_gives_the_code_we_sent()
    {
        using var harness = Harness();
        harness.Backend.Reports = request => Task.FromResult(FakeBackend.Answer(System.Net.HttpStatusCode.Created, new ReportErrorResponse(request.ReportBody.Code!, false)));

        var code = await ReportAsync(harness);

        Assert.Equal(Assert.Single(harness.Backend.ReportRequests).ReportBody.Code, code);
    }

    // ---- storm control: an outage must not turn into one report per request ----

    [Fact]
    public async Task One_defect_is_reported_at_most_the_budget_but_every_occurrence_gets_a_distinct_code_and_a_log_entry()
    {
        using var harness = Harness(("ErrorReporting:MaxPerFingerprintPerMinute", "3"));

        var codes = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            codes.Add(await ReportAsync(harness, ReporterHarness.Thrown($"occurrence {i}")));
        }

        var sent = harness.Backend.ReportRequests;
        Assert.Equal(3, sent.Count);
        Assert.Equal(codes.Take(3), sent.Select(r => r.ReportBody.Code));
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, ErrorCodeAssertions.AssertWellFormed);
        foreach (var refused in codes.Skip(3))
        {
            Assert.Contains(harness.Logs.Mentioning(refused), r => r.Level == LogLevel.Error);
        }
    }

    [Fact]
    public async Task Another_defect_has_a_budget_of_its_own()
    {
        using var harness = Harness(("ErrorReporting:MaxPerFingerprintPerMinute", "1"));

        await ReportAsync(harness, ReporterHarness.Thrown("first"));
        await ReportAsync(harness, ReporterHarness.Thrown("again, over budget"));
        var elsewhere = await ReportAsync(harness, ReporterHarness.ThrownElsewhere());

        Assert.Equal(2, harness.Backend.ReportRequests.Count);
        Assert.Equal(elsewhere, harness.Backend.ReportRequests[1].ReportBody.Code);
    }

    [Fact]
    public async Task The_same_failure_with_a_different_message_is_the_same_defect()
    {
        using var harness = Harness(("ErrorReporting:MaxPerFingerprintPerMinute", "1"));

        await ReportAsync(harness, ReporterHarness.Thrown("order 1001 failed"));
        await ReportAsync(harness, ReporterHarness.Thrown("order 1002 failed"));

        Assert.Single(harness.Backend.ReportRequests);
    }

    // ---- storm control over time: a one-minute window, driven by a clock the test moves ----

    [Fact]
    public async Task A_defect_over_its_budget_is_reported_again_once_the_minute_has_passed()
    {
        var clock = new ManualTimeProvider();
        using var harness = ReporterHarness.CreateWithClock(clock, ("ErrorReporting:MaxPerFingerprintPerMinute", "1"));

        await ReportAsync(harness, ReporterHarness.Thrown("first"));
        await ReportAsync(harness, ReporterHarness.Thrown("refused: over budget"));
        Assert.Single(harness.Backend.ReportRequests);

        clock.Advance(TimeSpan.FromSeconds(59));
        await ReportAsync(harness, ReporterHarness.Thrown("refused: 59 seconds are not a minute"));
        Assert.Single(harness.Backend.ReportRequests);

        clock.Advance(TimeSpan.FromSeconds(2));
        var again = await ReportAsync(harness, ReporterHarness.Thrown("allowed: 61 seconds after the first"));

        Assert.Equal(2, harness.Backend.ReportRequests.Count);
        Assert.Equal(again, harness.Backend.ReportRequests[1].ReportBody.Code);
    }

    [Fact]
    public async Task Occurrences_that_were_refused_do_not_extend_the_block()
    {
        var clock = new ManualTimeProvider();
        using var harness = ReporterHarness.CreateWithClock(clock, ("ErrorReporting:MaxPerFingerprintPerMinute", "2"));
        await ReportAsync(harness, ReporterHarness.Thrown("counts 1"));
        await ReportAsync(harness, ReporterHarness.Thrown("counts 2"));

        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await ReportAsync(harness, ReporterHarness.Thrown($"refused {i}"));
        }

        Assert.Equal(2, harness.Backend.ReportRequests.Count);

        clock.Advance(TimeSpan.FromSeconds(11)); // 61 seconds after the two that counted; the ten refusals in between must not
        await ReportAsync(harness, ReporterHarness.Thrown("allowed 1"));
        await ReportAsync(harness, ReporterHarness.Thrown("allowed 2"));

        Assert.Equal(4, harness.Backend.ReportRequests.Count);
    }

    /// <summary>
    /// Keeps every report the Web sends waiting inside the API until <paramref name="budget"/> of them have arrived (plus a moment for
    /// any that a racy guard lets through on top), then answers them all. A guard that checks the count and only then counts
    /// the report in lets everybody past the check while the first ones are still in flight; a sound one never has more than
    /// <paramref name="budget"/> in flight, so this does not slow it down.
    /// </summary>
    private static void HoldReportsUntilTheBudgetIsInFlight(ReporterHarness harness, int budget)
    {
        var arrived = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Backend.Reports = async request =>
        {
            if (Interlocked.Increment(ref arrived) == budget)
            {
                _ = Task.Delay(TimeSpan.FromMilliseconds(300)).ContinueWith(_ => release.TrySetResult(), TaskScheduler.Default);
            }

            await release.Task.WaitAsync(request.Cancellation);
            return FakeBackend.Answer(System.Net.HttpStatusCode.Created, new ReportErrorResponse(request.ReportBody.Code!, true));
        };
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Concurrent_occurrences_of_one_defect_never_exceed_the_budget(int round)
    {
        const int occurrences = 200;
        const int budget = 20;
        using var harness = Harness(("ErrorReporting:MaxPerFingerprintPerMinute", budget.ToString()));
        HoldReportsUntilTheBudgetIsInFlight(harness, budget);
        var start = new TaskCompletionSource();

        var pending = Enumerable.Range(0, occurrences).Select(i => Task.Run(async () =>
        {
            await start.Task.WaitAsync(TestTimeouts.Signal);
            return await ReportAsync(harness, ReporterHarness.Thrown($"occurrence {i}"));
        })).ToArray();
        start.SetResult();
        var codes = await Task.WhenAll(pending).WaitAsync(TestTimeouts.Operation);

        Assert.True(harness.Backend.ReportRequests.Count == budget, $"Round {round}: {harness.Backend.ReportRequests.Count} reports were sent; the budget is {budget}.");
        Assert.Equal(occurrences, codes.Distinct().Count());
        Assert.All(codes, ErrorCodeAssertions.AssertWellFormed);
    }

    [Fact]
    public async Task An_outage_of_the_api_costs_at_most_the_budget_in_attempts_however_many_requests_fail()
    {
        const int budget = 5;
        using var harness = Harness(("ErrorReporting:MaxPerFingerprintPerMinute", budget.ToString()));
        harness.Backend.Reports = FakeBackend.Refused!;

        var codes = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            codes.Add(await ReportAsync(harness, ReporterHarness.Thrown($"request {i}")));
        }

        Assert.Equal(budget, harness.Backend.ReportRequests.Count);
        Assert.Equal(40, codes.Distinct().Count());
    }

    // ---- many requests at once share nothing ----

    [Fact]
    public async Task Parallel_reports_keep_their_own_request_and_code_apart()
    {
        const int reports = 100;
        using var harness = Harness(("ErrorReporting:MaxPerFingerprintPerMinute", "1000"));
        var start = new TaskCompletionSource();

        var pending = Enumerable.Range(0, reports).Select(i => Task.Run(async () =>
        {
            await start.Task.WaitAsync(TestTimeouts.Signal);
            var code = await ReportAsync(harness, ReporterHarness.Thrown($"failure {i}"), ReporterHarness.Request("GET", $"/page-{i}", $"?n={i}"));
            return (Index: i, Code: code);
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(pending).WaitAsync(TestTimeouts.Operation);

        Assert.Equal(reports, results.Select(r => r.Code).Distinct().Count());
        var sent = harness.Backend.ReportRequests.Select(r => r.ReportBody).ToList();
        Assert.Equal(reports, sent.Count);
        foreach (var (index, code) in results)
        {
            var body = Assert.Single(sent, b => b.Code == code);
            Assert.Equal($"/page-{index}", body.Path);
            Assert.Contains($"n={index}", body.Query);
            Assert.Equal($"failure {index}", body.ExceptionMessage);
        }
    }

    [Fact]
    public async Task Parallel_reports_that_fail_in_different_ways_all_still_get_their_code()
    {
        using var harness = Harness(("ErrorReporting:TimeoutSeconds", "1"), ("ErrorReporting:MaxPerFingerprintPerMinute", "1000"));
        var failures = FakeBackend.ReportFailureNames.Where(n => n != "timeout").ToArray();
        var next = 0;
        harness.Backend.Reports = request => FakeBackend.ReportFailure(failures[Interlocked.Increment(ref next) % failures.Length])(request);

        var codes = await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(() => ReportAsync(harness, ReporterHarness.Thrown($"failure {i}"))))).WaitAsync(TestTimeouts.Operation);

        Assert.Equal(60, codes.Distinct().Count());
        Assert.All(codes, ErrorCodeAssertions.AssertWellFormed);
    }
}
