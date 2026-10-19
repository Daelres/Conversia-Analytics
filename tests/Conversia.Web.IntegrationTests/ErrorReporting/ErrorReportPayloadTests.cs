using System.Diagnostics;
using System.Text.Json;
using Conversia.TestSupport;
using Conversia.Web.IntegrationTests.Infrastructure;
using Conversia.Web.Services;

namespace Conversia.Web.IntegrationTests.ErrorReporting;

/// <summary>
/// What the Web tells the API about an error, as seen by the fake API: the request, the person and the service the support team needs
/// to find the problem, within the limits of the endpoint's contract and already sanitized; and never a cookie, a token, a form,
/// a header of the person or the shared key. The same Web host, the same pages, the same requests the person made.
/// </summary>
public class ErrorReportPayloadTests
{
    private const string Email = "ana@example.test";
    private const string UserAgent = "Mozilla/5.0 (error-report-tests)";

    private static void FailOnConversation(WebScenario scenario, string message)
        => scenario.Faults.Fault = (method, _) => method == nameof(IApiClient.GetConversationAsync) ? new InvalidOperationException(message) : null;

    private static Dictionary<string, string> RouteValues(string? json)
    {
        Assert.False(string.IsNullOrWhiteSpace(json), "routeValues is empty.");
        using var document = JsonDocument.Parse(json!);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    // ---- an unhandled exception of a signed-in person ----

    [Fact]
    public async Task The_report_of_a_failed_page_describes_the_request_the_person_and_the_service()
    {
        using var activities = new RequestActivities();
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync(Email);
        var conversationId = Guid.NewGuid();
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        FailOnConversation(scenario, "the conversation could not be shown");

        var page = await session.GetAsync(
            $"/conversaciones/{conversationId}?page=2&token=SECRET-QUERY-TOKEN",
            ("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01"),
            ("User-Agent", UserAgent));

        var code = ErrorCodeAssertions.AssertUnhandledPage(page);
        var body = Assert.Single(scenario.Backend.ReportRequests).ReportBody;
        var (userId, tenantId) = FakeBackend.IdentityOf(Email);
        Assert.Equal(code, body.Code);
        Assert.Equal("Unhandled", body.Kind);
        Assert.Equal("GET", body.HttpMethod);
        Assert.Equal($"/conversaciones/{conversationId}", body.Path);
        Assert.Contains("page=2", body.Query);
        Assert.DoesNotContain("SECRET-QUERY-TOKEN", body.Query);
        Assert.Equal(500, body.StatusCode);
        Assert.Equal(userId, body.UserId);
        Assert.Equal(tenantId, body.TenantId);
        Assert.Equal(traceId, body.TraceId);
        Assert.False(string.IsNullOrWhiteSpace(body.RequestId));
        Assert.Equal(UserAgent, body.UserAgent);
        Assert.Equal("System.InvalidOperationException", body.ExceptionType);
        Assert.Equal("the conversation could not be shown", body.ExceptionMessage);
        Assert.Contains("InvalidOperationException: the conversation could not be shown", body.ExceptionDetails);
        Assert.Contains("   at ", body.ExceptionDetails);
        Assert.Equal("Production", body.Environment);
        Assert.False(string.IsNullOrWhiteSpace(body.ServiceVersion));
        Assert.False(string.IsNullOrWhiteSpace(body.Instance));
        ReportContract.AssertValidForTheApi(body);
    }

    [Fact]
    public async Task The_report_names_the_endpoint_that_failed_and_its_route_values_not_the_error_page_that_answered()
    {
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync(Email);
        var conversationId = Guid.NewGuid();
        FailOnConversation(scenario, "boom");

        await session.GetAsync($"/conversaciones/{conversationId}");

        var body = Assert.Single(scenario.Backend.ReportRequests).ReportBody;
        Assert.False(string.IsNullOrWhiteSpace(body.Endpoint), "No endpoint was reported.");
        Assert.Contains("conversa", body.Endpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Error", body.Endpoint);
        Assert.Equal(conversationId.ToString(), RouteValues(body.RouteValues)["id"], ignoreCase: true);
        Assert.Equal($"/conversaciones/{conversationId}", body.Path);
    }

    [Fact]
    public async Task A_failed_form_post_reports_the_post_without_anything_that_was_typed()
    {
        using var scenario = WebScenario.Create();
        using var session = scenario.Anonymous();

        var page = await UnhandledScenarios.TriggerAsync(scenario, "POST /login", existing: session);

        var code = ErrorCodeAssertions.AssertUnhandledPage(page);
        var request = Assert.Single(scenario.Backend.ReportRequests);
        var body = request.ReportBody;
        Assert.Equal(code, body.Code);
        Assert.Equal("POST", body.HttpMethod);
        Assert.Equal("/login", body.Path);
        Assert.Null(body.UserId);
        Assert.Null(body.TenantId);
        foreach (var typed in new[] { "ana@example.test", WebScenario.Password })
        {
            Assert.DoesNotContain(typed, request.Body);
        }
    }

    [Fact]
    public async Task Nothing_the_person_sent_in_a_cookie_header_or_form_and_not_the_key_reaches_the_report()
    {
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync(Email);
        var users = await WebFlows.UsersAsync(session);
        var form = FormModel.From(users, users.FindPasswordForm()).Set("email", "new-person@example.test").Set("password", "Typed-Pa55word!");
        scenario.BreakTheWeb();

        var page = await session.SubmitAsync(form);

        ErrorCodeAssertions.AssertUnhandledPage(page);
        var request = Assert.Single(scenario.Backend.ReportRequests);
        var secrets = session.AuthCookies.Select(c => c.Value)
            .Concat(scenario.Host.Api.IssuedTokens)
            .Append("Typed-Pa55word!").Append("new-person@example.test").Append(form.Get(FormModel.AntiforgeryField)!)
            .Append(WebScenario.Key).Append(WebScenario.Password).ToList();
        Assert.True(secrets.Count >= 6 && secrets.All(s => s.Length > 5), "The test did not collect the secrets it means to look for.");
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, request.Body);
        }

        Assert.False(PageResponse.ContainsJwt(request.Body!));
        Assert.Null(request.Header("Cookie"));
        Assert.Null(request.Header("Authorization"));
        Assert.Equal(WebScenario.Key, request.Header("X-Error-Report-Key"));
    }

    [Fact]
    public async Task The_key_is_in_no_log_and_no_page()
    {
        using var scenario = WebScenario.Create();
        scenario.Backend.Reports = FakeBackend.ReportFailure("401");

        var page = await UnhandledScenarios.TriggerAsync(scenario, "GET /users");

        ErrorCodeAssertions.AssertUnhandledPage(page);
        Assert.DoesNotContain(WebScenario.Key, page.Body);
        Assert.DoesNotContain(scenario.Host.Logs.Entries, entry => entry.Contains(WebScenario.Key));
        Assert.DoesNotContain(scenario.Host.Logs.Records, record => record.Mentions(WebScenario.Key));
    }

    // ---- sanitized before it leaves the Web ----

    [Theory]
    [MemberData(nameof(ReportContract.SecretsInMessages), MemberType = typeof(ReportContract))]
    public async Task A_secret_in_the_message_of_the_exception_is_redacted_in_the_report(string message, string secret)
    {
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync(Email);
        FailOnConversation(scenario, message);

        var page = await session.GetAsync($"/conversaciones/{Guid.NewGuid()}");

        ErrorCodeAssertions.AssertUnhandledPage(page);
        var request = Assert.Single(scenario.Backend.ReportRequests);
        Assert.DoesNotContain(secret, request.Body);
        Assert.DoesNotContain(secret, page.Body);
        ReportContract.AssertRedactionMarked(request.ReportBody);
    }

    [Fact]
    public async Task Sensitive_query_values_are_redacted_and_the_others_are_kept()
    {
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync(Email);
        scenario.BreakTheWeb();

        await session.GetAsync("/conversaciones?search=tornillo&page=3&access_token=ACCESS-VALUE-1&password=HUNTER-2&sig=SIG-VALUE-3");

        var request = Assert.Single(scenario.Backend.ReportRequests);
        foreach (var secret in new[] { "ACCESS-VALUE-1", "HUNTER-2", "SIG-VALUE-3" })
        {
            Assert.DoesNotContain(secret, request.Body);
        }

        Assert.Contains("search=tornillo", request.ReportBody.Query);
        Assert.Contains("page=3", request.ReportBody.Query);
    }

    // ---- the contract's limits ----

    [Fact]
    public async Task Enormous_requests_and_messages_are_cut_to_what_the_api_accepts()
    {
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync(Email);
        FailOnConversation(scenario, new string('m', 60_000));
        var query = string.Join("&", Enumerable.Range(0, 600).Select(i => $"key{i}=value{i}"));

        var page = await session.GetAsync($"/conversaciones/{Guid.NewGuid()}?{query}", ("User-Agent", new string('u', 3_000)));

        ErrorCodeAssertions.AssertUnhandledPage(page);
        var request = Assert.Single(scenario.Backend.ReportRequests);
        using var document = request.BodyJson();
        foreach (var (field, limit) in ErrorLimits.RefusedWhenLongerReportFields)
        {
            if (document.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
            {
                Assert.True(value.GetString()!.Length <= limit, $"'{field}' has {value.GetString()!.Length} characters; the API refuses more than {limit}.");
            }
        }

        Assert.True(document.RootElement.GetProperty("exceptionDetails").GetString()!.Length <= ErrorLimits.ExceptionDetailsCap);
        Assert.False(string.IsNullOrWhiteSpace(request.ReportBody.Path));
        ReportContract.AssertValidForTheApi(request.ReportBody);
    }

    // ---- failures of the API: the same enrichment, from the request that was being served ----

    [Fact]
    public async Task A_report_about_the_api_names_the_request_the_person_made_and_the_call_that_failed()
    {
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync(Email);
        scenario.BreakTheApi("connection refused");

        var page = await session.GetAsync("/users?token=SECRET-QUERY-TOKEN", ("User-Agent", UserAgent));

        var shown = ErrorCodeAssertions.Shown(page);
        var (userId, tenantId) = FakeBackend.IdentityOf(Email);
        var body = scenario.Backend.ReportRequests.Select(r => r.ReportBody).First(b => b.Code == shown);
        Assert.Equal("UpstreamUnavailable", body.Kind);
        Assert.Equal("GET", body.HttpMethod);
        Assert.Equal("/users", body.Path);
        Assert.DoesNotContain("SECRET-QUERY-TOKEN", body.Query ?? string.Empty);
        Assert.Equal(UserAgent, body.UserAgent);
        Assert.Equal(userId, body.UserId);
        Assert.Equal(tenantId, body.TenantId);
        Assert.Equal("System.Net.Http.HttpRequestException", body.ExceptionType);
        Assert.Contains("api/users", body.Context);
        ReportContract.AssertValidForTheApi(body);
    }

    [Fact]
    public async Task The_upstream_report_never_carries_the_bearer_token_or_the_filter_values()
    {
        using var scenario = WebScenario.Create();
        using var session = await scenario.SignInAsync(Email);
        scenario.BreakTheApi("503 empty");

        var privateBranch = Guid.NewGuid().ToString();
        var page = await session.GetAsync($"/conversaciones?branch={privateBranch}");

        ErrorCodeAssertions.Shown(page);
        Assert.NotEmpty(scenario.Backend.ReportRequests);
        foreach (var request in scenario.Backend.ReportRequests)
        {
            Assert.DoesNotContain(scenario.Host.Api.IssuedTokens.First(), request.Body);
            Assert.False(PageResponse.ContainsJwt(request.Body!));
            Assert.Null(request.Header("Authorization"));
            Assert.DoesNotContain(privateBranch, request.ReportBody.Context ?? string.Empty);
            ReportContract.AssertValidForTheApi(request.ReportBody);
        }
    }

    [Fact]
    public async Task A_failure_with_the_api_down_during_login_reports_no_credentials()
    {
        using var scenario = WebScenario.Create();
        scenario.BreakTheApi("connection refused");

        var page = await UnhandledScenarios.TriggerAsync(scenario, "POST /login", breakIt: s => s.BreakTheApi("connection refused"));

        ErrorCodeAssertions.Shown(page);
        var request = Assert.Single(scenario.Backend.ReportRequests);
        Assert.DoesNotContain(WebScenario.Password, request.Body);
        Assert.DoesNotContain("ana@example.test", request.Body);
        Assert.Equal("/login", request.ReportBody.Path);
        Assert.Null(request.ReportBody.UserId);
    }
}
