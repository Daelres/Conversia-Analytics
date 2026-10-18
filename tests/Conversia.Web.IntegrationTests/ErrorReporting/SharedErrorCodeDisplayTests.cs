using System.Net;
using Conversia.Domain.Common;
using Conversia.TestSupport;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.ErrorReporting;

/// <summary>
/// Wherever the Web tells a person "el servicio no está disponible" it also shows the code of that error, through the shared
/// components, so a page needs nothing of its own to show it: this is proven on every page and form of the Web, not on one.
/// The code is the API's own when the API already registered the failure (then the Web does not report it again), otherwise one
/// the Web generated and reported (kind <c>UpstreamUnavailable</c> when the API cannot be reached or answers 502/503/504,
/// <c>UpstreamBadResponse</c> when it answers something unusable). Known outcomes (403, 404, 429, validation...) show no code.
/// Driven through the real Web host against a fake API.
/// </summary>
public class SharedErrorCodeDisplayTests
{
    private static string NewCode() => ErrorCode.New(DateTime.UtcNow);

    public static TheoryData<string> Pages => UnhandledScenarios.Pages;

    private static void AssertShownInAnAlert(PageResponse page)
    {
        var element = Assert.Single(ErrorCodeAssertions.Elements(page));
        Assert.True(element.Closest("[role='alert']") is not null, $"The code on {page.RequestUri.PathAndQuery} is not announced: it is not inside an element with role=\"alert\".");
    }

    // ---- the API already registered the failure: its code is shown, nothing is reported again ----

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task A_500_with_the_apis_code_shows_that_code_on_every_page_and_is_not_reported_again(string pageName)
    {
        using var scenario = WebScenario.Create();
        var code = NewCode();

        var page = await UnhandledScenarios.TriggerAsync(scenario, pageName, breakIt: s => s.BreakTheApi(_ => Task.FromResult<HttpResponseMessage?>(FakeBackend.UnexpectedWithCode(code))));

        Assert.Equal(code, ErrorCodeAssertions.Shown(page));
        AssertShownInAnAlert(page);
        Assert.Contains(SpanishText.Unavailable, page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(scenario.Backend.ReportRequests);
        WebFlows.AssertNoStackTrace(page);
        var report = new SpanishUiReport();
        report.Check(pageName, page);
        report.AssertClean();
    }

    [Fact]
    public async Task The_apis_code_is_in_the_log()
    {
        using var scenario = WebScenario.Create();
        var code = NewCode();

        await UnhandledScenarios.TriggerAsync(scenario, "GET /users", breakIt: s => s.BreakTheApi(_ => Task.FromResult<HttpResponseMessage?>(FakeBackend.UnexpectedWithCode(code))));

        Assert.Contains(scenario.Host.Logs.Mentioning(code), r => r.Level >= Microsoft.Extensions.Logging.LogLevel.Information);
    }

    [Fact]
    public async Task Two_failed_calls_of_one_render_show_one_code_one_of_the_two()
    {
        using var scenario = WebScenario.Create();
        var (first, second) = (NewCode(), NewCode());

        var page = await UnhandledScenarios.TriggerAsync(scenario, "GET /products/{id}/edit", breakIt: s => s.BreakTheApi(request =>
            Task.FromResult<HttpResponseMessage?>(FakeBackend.UnexpectedWithCode(request.Path == "/api/users/me" ? first : second))));

        Assert.Contains(ErrorCodeAssertions.Shown(page), new[] { first, second });
        Assert.Empty(scenario.Backend.ReportRequests);
    }

    [Fact]
    public async Task An_error_id_that_is_not_a_code_is_never_printed_the_webs_own_code_is_shown_instead()
    {
        using var scenario = WebScenario.Create();
        const string hostile = "<script>alert('xss')</script>";

        var page = await UnhandledScenarios.TriggerAsync(scenario, "GET /users", breakIt: s => s.BreakTheApi(_ => Task.FromResult<HttpResponseMessage?>(FakeBackend.Raw(
            HttpStatusCode.InternalServerError, System.Text.Json.JsonSerializer.Serialize(new { title = "x", status = 500, errorId = hostile })))));

        var code = ErrorCodeAssertions.Shown(page);
        Assert.DoesNotContain("alert('xss')", page.Body);
        Assert.DoesNotContain("<script>alert", page.Body);
        Assert.Contains(code, scenario.Backend.ReportRequests.Select(r => r.ReportBody.Code));
    }

    // ---- the API is down or unusable: a code of the Web's own, reported ----

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task With_the_api_down_every_page_shows_a_code_that_was_reported_as_upstream_unavailable(string pageName)
    {
        using var scenario = WebScenario.Create();

        var page = await UnhandledScenarios.TriggerAsync(scenario, pageName, breakIt: s => s.BreakTheApi("connection refused"));

        var code = ErrorCodeAssertions.Shown(page);
        AssertShownInAnAlert(page);
        Assert.Contains(SpanishText.Unavailable, page.Text, StringComparison.OrdinalIgnoreCase);
        var reports = scenario.Backend.ReportRequests.Select(r => r.ReportBody).ToList();
        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal("UpstreamUnavailable", r.Kind));
        Assert.Contains(code, reports.Select(r => r.Code));
        var shown = Assert.Single(reports, r => r.Code == code);
        Assert.NotNull(shown.ExceptionType);
        var report = new SpanishUiReport();
        report.Check(pageName, page);
        report.AssertClean();
    }

    [Theory]
    [MemberData(nameof(FakeBackend.UpstreamFailures), MemberType = typeof(FakeBackend))]
    public async Task Every_way_the_api_can_fail_shows_a_code_that_was_reported(string failure)
    {
        foreach (var pageName in new[] { "GET /products", "GET /", "POST /login" })
        {
            using var scenario = WebScenario.Create();

            var page = await UnhandledScenarios.TriggerAsync(scenario, pageName, breakIt: s => s.BreakTheApi(failure));

            var code = ErrorCodeAssertions.Shown(page);
            var reports = scenario.Backend.ReportRequests.Select(r => r.ReportBody).ToList();
            Assert.True(reports.Any(r => r.Code == code), $"{pageName} / {failure}: the code {code} was not reported (reported: {string.Join(", ", reports.Select(r => r.Code))}).");
            var expectedKind = failure is "connection refused" or "timeout" or "502 html" or "503 empty" or "504 empty" ? "UpstreamUnavailable" : "UpstreamBadResponse";
            Assert.True(reports.Single(r => r.Code == code).Kind == expectedKind, $"{pageName} / {failure}: wrong kind of report.");
        }
    }

    [Fact]
    public async Task A_failed_report_does_not_take_the_code_off_the_page()
    {
        using var scenario = WebScenario.Create();
        scenario.Backend.Reports = FakeBackend.Refused!;

        var page = await UnhandledScenarios.TriggerAsync(scenario, "GET /users", breakIt: s => s.BreakTheApi("connection refused"));

        var code = ErrorCodeAssertions.Shown(page);
        Assert.Contains(scenario.Host.Logs.Mentioning(code), r => r.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task Without_an_error_reporting_key_the_page_still_shows_a_code_and_nothing_is_sent()
    {
        using var scenario = WebScenario.Create(("ErrorReporting:ApiKey", null));

        var page = await UnhandledScenarios.TriggerAsync(scenario, "GET /users", breakIt: s => s.BreakTheApi("connection refused"));

        var code = ErrorCodeAssertions.Shown(page);
        Assert.Empty(scenario.Backend.ReportRequests);
        Assert.Contains(scenario.Host.Logs.Mentioning(code), r => r.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task An_outage_does_not_report_every_request_but_every_person_sees_a_code()
    {
        using var scenario = WebScenario.Create(("ErrorReporting:MaxPerFingerprintPerMinute", "4"));
        scenario.BreakTheApi("connection refused");
        using var session = scenario.Anonymous();

        var codes = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var login = (await session.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
            var form = FormModel.From(login, login.FindPasswordForm()).Set("email", "a@example.test").Set("password", WebScenario.Password);
            codes.Add(ErrorCodeAssertions.Shown(await session.SubmitAsync(form)));
        }

        Assert.Equal(12, codes.Distinct().Count());
        Assert.Equal(4, scenario.Backend.ReportRequests.Count);
    }

    // ---- known outcomes are not errors ----

    public static TheoryData<int> KnownStatuses => new() { 400, 403, 404, 409, 429 };

    [Theory]
    [MemberData(nameof(KnownStatuses))]
    public async Task Known_outcomes_show_no_code_and_report_nothing(int status)
    {
        foreach (var pageName in new[] { "GET /users", "GET /products", "GET /", "POST /login", "POST /users" })
        {
            using var scenario = WebScenario.Create();

            var page = await UnhandledScenarios.TriggerAsync(scenario, pageName, breakIt: s => s.BreakTheApi(_ => Task.FromResult<HttpResponseMessage?>(
                FakeBackend.Raw((HttpStatusCode)status, """{"title":"x","status":0,"errors":{"Email":["Correo no válido."]}}"""))));

            ErrorCodeAssertions.NotShown(page);
            Assert.True(scenario.Backend.ReportRequests.Count == 0, $"{pageName} / {status}: a known outcome was reported.");
        }
    }

    [Fact]
    public async Task A_healthy_page_after_a_failed_one_shows_no_code()
    {
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync();
        scenario.BreakTheApi("connection refused");
        var failed = (await session.GetAsync("/users"));
        ErrorCodeAssertions.Shown(failed);

        scenario.HealTheApi();
        var healthy = (await session.GetAsync("/users")).ShouldBe(HttpStatusCode.OK);

        ErrorCodeAssertions.NotShown(healthy);
    }

    // ---- many people at once ----

    [Fact]
    public async Task One_persons_failure_never_shows_on_another_persons_page()
    {
        const int people = 20;
        using var scenario = WebScenario.Create(("ErrorReporting:MaxPerFingerprintPerMinute", "1000"));
        var emails = Enumerable.Range(0, people).Select(i => $"person-{i}@example.test").ToArray();
        var sessions = new List<WebSession>();
        foreach (var email in emails)
        {
            sessions.Add(await scenario.SignInAsync(email));
        }

        var failing = emails.Where((_, i) => i % 2 == 0).Select(e => FakeBackend.IdentityOf(e).UserId).ToHashSet();
        scenario.BreakTheApi(request =>
        {
            var user = FakeJwt.IdentityOf(request.Header("Authorization")?["Bearer ".Length..]).UserId;
            return Task.FromResult<HttpResponseMessage?>(failing.Contains(user) ? FakeBackend.Raw(HttpStatusCode.ServiceUnavailable, null) : null);
        });

        var start = new TaskCompletionSource();
        var pending = sessions.Select(session => Task.Run(async () =>
        {
            await start.Task.WaitAsync(TestTimeouts.Signal);
            return await session.GetAsync("/users");
        })).ToArray();
        start.SetResult();
        var pages = await Task.WhenAll(pending).WaitAsync(TestTimeouts.Operation);

        var codes = new List<string>();
        for (var i = 0; i < people; i++)
        {
            if (i % 2 == 0)
            {
                codes.Add(ErrorCodeAssertions.Shown(pages[i]));
            }
            else
            {
                pages[i].ShouldBe(HttpStatusCode.OK);
                ErrorCodeAssertions.NotShown(pages[i]);
            }
        }

        Assert.Equal(people / 2, codes.Distinct().Count());
        var reported = scenario.Backend.ReportRequests.Select(r => r.ReportBody.Code!).ToList();
        Assert.Equal(reported.Count, reported.Distinct().Count()); // two calls fail per render: each has its own code
        Assert.All(codes, code => Assert.Contains(code, reported));
    }
}
