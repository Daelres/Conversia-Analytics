using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Conversia.Domain.Common;
using Conversia.TestSupport;
using Conversia.Web.IntegrationTests.Infrastructure;
using Conversia.Web.Services;
using Microsoft.Extensions.Logging;

namespace Conversia.Web.IntegrationTests.ErrorReporting;

/// <summary>
/// An exception nobody handled, anywhere in the Web (a page read, an enhanced navigation, a form post), ends in the Web's own error
/// page: HTTP 500, no internals, in Spanish, with "Código de error: ERR-..." (and the same code in the <c>X-Error-Id</c> header) that
/// the person can quote in a ticket. The code is reported to the API best effort; whatever the API does — or does not — the person
/// still gets a code, the original error is logged with it and is never masked. Driven through the real Web host against a fake API.
/// </summary>
public class WebExceptionHandlerTests
{
    private static IReadOnlyList<CapturedLogs.LogRecord> ErrorsMentioning(WebScenario scenario, string code)
        => scenario.Host.Logs.Mentioning(code).Where(r => r.Level == LogLevel.Error).ToList();

    // ---- what the person gets ----

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task An_unhandled_exception_ends_in_a_500_error_page_with_a_code_the_person_can_quote(string scenarioName)
    {
        using var scenario = WebScenario.Create();

        var page = await UnhandledScenarios.TriggerAsync(scenario, scenarioName);

        ErrorCodeAssertions.AssertUnhandledPage(page);
        Assert.DoesNotContain(WebScenario.SecretMessage, page.Body);
        Assert.DoesNotContain("InvalidOperationException", page.Body);
        Assert.False(PageResponse.ContainsJwt(page.Body));
        WebFlows.AssertNotCacheable(page);
        var report = new SpanishUiReport();
        report.Check($"unhandled exception ({scenarioName})", page);
        report.AssertClean();
    }

    public static TheoryData<string> Scenarios => UnhandledScenarios.All;

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task The_failure_is_reported_once_with_the_code_the_person_sees_and_the_code_is_in_the_log(string scenarioName)
    {
        using var scenario = WebScenario.Create();

        var page = await UnhandledScenarios.TriggerAsync(scenario, scenarioName);

        var code = ErrorCodeAssertions.AssertUnhandledPage(page);
        var request = Assert.Single(scenario.Backend.ReportRequests);
        Assert.Equal(WebScenario.Key, request.Header("X-Error-Report-Key"));
        Assert.Equal(code, request.ReportBody.Code);
        Assert.Equal("Unhandled", request.ReportBody.Kind);
        Assert.Contains(scenario.Host.Logs.Mentioning(code), r => r.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task The_error_page_is_only_for_failures_a_healthy_page_has_no_code_header_or_report()
    {
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync();

        var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        var users = (await session.GetAsync("/users")).ShouldBe(HttpStatusCode.OK);

        foreach (var page in new[] { home, users })
        {
            ErrorCodeAssertions.NotShown(page);
            Assert.Null(page.Header(ErrorCodeAssertions.HeaderName));
        }

        Assert.Empty(scenario.Backend.ReportRequests);
        Assert.DoesNotContain(scenario.Host.Logs.Records, r => r.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Opening_the_error_page_directly_shows_it_without_a_code_and_without_reporting_anything()
    {
        using var scenario = WebScenario.Create();
        using var session = scenario.Anonymous();

        var page = await session.GetAsync("/Error");

        page.ShouldBe(HttpStatusCode.OK);
        ErrorCodeAssertions.NotShown(page);
        Assert.Null(page.Header(ErrorCodeAssertions.HeaderName));
        Assert.Empty(scenario.Backend.Requests);
    }

    [Fact]
    public async Task Each_occurrence_gets_a_code_of_its_own()
    {
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync();
        scenario.BreakTheWeb();

        var codes = new List<string>();
        for (var i = 0; i < 15; i++)
        {
            codes.Add(ErrorCodeAssertions.AssertUnhandledPage(await session.GetAsync("/users")));
        }

        Assert.Equal(15, codes.Distinct().Count());
    }

    // ---- outcomes that are not errors ----

    [Fact]
    public async Task Known_outcomes_are_not_errors_no_code_no_header_no_report()
    {
        using var scenario = WebScenario.Create();
        using var anonymous = scenario.Anonymous();
        using var owner = await scenario.SignInAsync();
        scenario.BreakTheApi(request => Task.FromResult<HttpResponseMessage?>(FakeBackend.Raw(HttpStatusCode.Forbidden, """{"title":"x","status":403}""")));

        var pages = new[]
        {
            (await anonymous.GetAsync("/no-such-page")).ShouldBe(HttpStatusCode.NotFound),
            await anonymous.PostRawAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.Email"] = "a@example.test" })), // no antiforgery token
            await owner.GetAsync("/users"), // the API says 403
        };

        Assert.Contains(pages, p => p.Status == 400);
        Assert.Contains(pages, p => p.Status == 403);
        foreach (var page in pages)
        {
            ErrorCodeAssertions.NotShown(page);
            Assert.Null(page.Header(ErrorCodeAssertions.HeaderName));
        }

        Assert.Empty(scenario.Backend.ReportRequests);
    }

    // ---- the API cannot take the report: the person still has a code, the error is not masked ----

    [Theory]
    [MemberData(nameof(FakeBackend.ReportFailures), MemberType = typeof(FakeBackend))]
    public async Task Whatever_the_api_answers_to_the_report_the_person_gets_the_error_page_with_a_code(string failure)
    {
        using var scenario = WebScenario.Create(("ErrorReporting:TimeoutSeconds", "1"));
        scenario.Backend.Reports = FakeBackend.ReportFailure(failure);

        var page = await UnhandledScenarios.TriggerAsync(scenario, "GET /users").WaitAsync(TestTimeouts.Operation);

        var code = ErrorCodeAssertions.AssertUnhandledPage(page);
        Assert.True(scenario.Backend.ReportRequests.Count == 1, $"{failure}: expected one attempt, saw {scenario.Backend.ReportRequests.Count}.");
        if (FakeBackend.ReachedTheApi(failure))
        {
            Assert.Contains(scenario.Host.Logs.Mentioning(code), r => r.Level >= LogLevel.Information);
        }
        else
        {
            Assert.Contains(ErrorsMentioning(scenario, code), r => r.Mentions(WebScenario.SecretMessage));
        }

        Assert.DoesNotContain(WebScenario.Key, page.Body);
        Assert.DoesNotContain(scenario.Host.Logs.Entries, entry => entry.Contains(WebScenario.Key));
        Assert.DoesNotContain(WebScenario.SecretMessage, page.Body);
    }

    [Fact]
    public async Task A_hung_api_holds_the_error_page_back_by_no_more_than_the_timeout_and_the_hung_request_is_cancelled()
    {
        using var scenario = WebScenario.Create(("ErrorReporting:TimeoutSeconds", "1"));
        using var session = await scenario.SignInAsync();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Backend.Reports = request =>
        {
            request.Cancellation.Register(() => cancelled.TrySetResult());
            return FakeBackend.HangAsync(request.Cancellation);
        };
        scenario.BreakTheWeb();

        var clock = Stopwatch.StartNew();
        var page = await session.GetAsync("/users").WaitAsync(TestTimeouts.Operation);
        var held = clock.Elapsed;

        ErrorCodeAssertions.AssertUnhandledPage(page);
        Assert.True(held <= TimeSpan.FromSeconds(1) + TestTimeouts.Slack, $"The error page took {held.TotalSeconds:0.0} s with a 1 s timeout.");
        await cancelled.Task.WaitAsync(TestTimeouts.Signal); // the Web gave up: the hung request was cancelled, not left running
    }

    public static TheoryData<string, string?> NotReporting => new()
    {
        { "ErrorReporting:ApiKey", null },
        { "ErrorReporting:ApiKey", "" },
        { "ErrorReporting:Enabled", "false" },
    };

    [Theory]
    [MemberData(nameof(NotReporting))]
    public async Task Without_a_key_or_with_reporting_disabled_the_person_still_gets_a_code_and_the_log_has_it(string setting, string? value)
    {
        using var scenario = WebScenario.Create((setting, value));

        var page = await UnhandledScenarios.TriggerAsync(scenario, "GET /users");

        var code = ErrorCodeAssertions.AssertUnhandledPage(page);
        Assert.Empty(scenario.Backend.ReportRequests);
        Assert.Contains(ErrorsMentioning(scenario, code), r => r.Mentions(WebScenario.SecretMessage));
    }

    [Fact]
    public async Task The_code_the_api_stored_is_the_one_the_person_is_shown()
    {
        using var scenario = WebScenario.Create();
        var stored = ErrorCode.New(DateTime.UtcNow);
        scenario.Backend.Reports = _ => Task.FromResult(FakeBackend.Answer(HttpStatusCode.Created, new { code = stored, persisted = true }));

        var page = await UnhandledScenarios.TriggerAsync(scenario, "GET /users");

        Assert.Equal(stored, ErrorCodeAssertions.AssertUnhandledPage(page));
    }

    // ---- storm control ----

    [Fact]
    public async Task A_burst_of_the_same_failure_is_reported_up_to_the_budget_yet_every_person_gets_a_code()
    {
        using var scenario = WebScenario.Create(("ErrorReporting:MaxPerFingerprintPerMinute", "3"));
        using var session = await scenario.SignInAsync();
        scenario.BreakTheWeb();

        var codes = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            codes.Add(ErrorCodeAssertions.AssertUnhandledPage(await session.GetAsync("/users")));
        }

        Assert.Equal(10, codes.Distinct().Count());
        Assert.Equal(3, scenario.Backend.ReportRequests.Count);
        foreach (var refused in codes.Skip(3))
        {
            Assert.Contains(ErrorsMentioning(scenario, refused), r => r.Mentions(WebScenario.SecretMessage));
        }
    }

    // ---- many people at once ----

    [Fact]
    public async Task Simultaneous_failures_get_distinct_codes_and_each_report_carries_its_own_request_and_person()
    {
        const int people = 30;
        using var scenario = WebScenario.Create(("ErrorReporting:MaxPerFingerprintPerMinute", "1000"));
        var branches = Enumerable.Range(0, people).Select(_ => Guid.NewGuid()).ToArray();
        scenario.Faults.Fault = (method, args) => method == nameof(IApiClient.GetConversationsAsync)
            ? new InvalidOperationException($"boom {((Conversia.Application.DTOs.Conversations.ConversationQuery)args[1]!).BranchId}")
            : null;
        var emails = Enumerable.Range(0, people).Select(i => $"person-{i}@example.test").ToArray();
        var sessions = new List<WebSession>();
        foreach (var email in emails)
        {
            sessions.Add(await scenario.SignInAsync(email));
        }

        var start = new TaskCompletionSource();
        var pending = sessions.Select((session, i) => Task.Run(async () =>
        {
            await start.Task.WaitAsync(TestTimeouts.Signal);
            return await session.GetAsync($"/conversaciones?branch={branches[i]}");
        })).ToArray();
        start.SetResult();
        var pages = await Task.WhenAll(pending).WaitAsync(TestTimeouts.Operation);

        var codes = pages.Select(ErrorCodeAssertions.AssertUnhandledPage).ToArray();
        Assert.Equal(people, codes.Distinct().Count());
        var reports = scenario.Backend.ReportRequests.Select(r => r.ReportBody).ToList();
        Assert.Equal(people, reports.Count);
        for (var i = 0; i < people; i++)
        {
            var report = Assert.Single(reports, r => r.Code == codes[i]);
            var (user, tenant) = FakeBackend.IdentityOf(emails[i]);
            Assert.Equal($"boom {branches[i]}", report.ExceptionMessage);
            Assert.Contains(branches[i].ToString(), report.Query);
            Assert.Equal(user, report.UserId);
            Assert.Equal(tenant, report.TenantId);
        }
    }

    // ---- requests that are not failures of the Web, or whose answer can no longer be an error page ----

    [Fact]
    public async Task A_request_the_browser_abandoned_is_neither_reported_nor_logged_as_an_error()
    {
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync();
        var path = $"/users?abandoned={Guid.NewGuid():N}";
        var reachedTheApi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Faults.Fault = (method, _) =>
        {
            if (method != nameof(IApiClient.GetUsersAsync))
            {
                return null;
            }

            var aborted = (RequestProbe.Current ?? throw new InvalidOperationException("The fault ran outside a request.")).RequestAborted;
            reachedTheApi.TrySetResult();
            aborted.WaitHandle.WaitOne(TestTimeouts.Signal); // the call is cancelled once the browser has gone, as an in-flight call is
            return new OperationCanceledException("the call was cancelled because the browser went away", aborted);
        };
        using var browser = new CancellationTokenSource();

        var request = session.GetAbortableAsync(path, browser.Token);
        await reachedTheApi.Task.WaitAsync(TestTimeouts.Signal);
        await browser.CancelAsync();
        await Record.ExceptionAsync(() => request); // the browser's side of it
        await scenario.Requests.FinishedAsync(path);

        Assert.Empty(scenario.Backend.ReportRequests);
        Assert.DoesNotContain(scenario.Host.Logs.Records, r => r.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task A_failure_after_the_response_has_started_is_reported_at_most_once_and_the_handler_adds_no_failure_of_its_own()
    {
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync();
        var path = $"/users?started={Guid.NewGuid():N}";
        scenario.Faults.Fault = (method, _) =>
        {
            if (method != nameof(IApiClient.GetUsersAsync))
            {
                return null;
            }

            var response = (RequestProbe.Current ?? throw new InvalidOperationException("The fault ran outside a request.")).Response;
            response.StatusCode = 200;
            response.ContentType = "text/html";
            response.WriteAsync("<html><body>the first part of the page").GetAwaiter().GetResult();
            response.Body.FlushAsync().GetAwaiter().GetResult(); // from here on the status and the headers are gone
            return new InvalidOperationException(WebScenario.SecretMessage);
        };

        // The server may or may not hand the broken response to the browser; what must not happen is a failure the handler adds.
        var failure = await Record.ExceptionAsync(() => session.GetAsync(path));
        await scenario.Requests.FinishedAsync(path);

        Assert.True(scenario.Backend.ReportRequests.Count <= 1, $"The failure was reported {scenario.Backend.ReportRequests.Count} times.");
        if (scenario.Backend.ReportRequests.Count == 1)
        {
            Assert.Equal("Unhandled", scenario.Backend.ReportRequests[0].ReportBody.Kind);
        }

        var handlerFailures = failure is null
            ? new List<Exception>()
            : ExceptionChain.Flatten(failure).Where(e => e is NullReferenceException or ObjectDisposedException || e.Message.Contains("read-only") || e.Message.Contains("already started")).ToList();
        Assert.True(handlerFailures.Count == 0, "The handler failed on its own while handling the failure: " + (failure is null ? string.Empty : ExceptionChain.Describe(failure)));
    }
}
