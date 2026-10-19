extern alias ApiHost;

using System.Net;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Conversia.TestSupport;
using Conversia.Web.IntegrationTests.Infrastructure;
using Conversia.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.ErrorReporting;

/// <summary>
/// The Web and the real API together, on a real PostgreSQL: an error the Web reports ends up in <c>ErrorLogs</c> under the very code
/// the person was shown, as a row of the Web, with the person's tenant and user; and when the API refuses the report (wrong key, no
/// key configured) the person still has a code and the error is in the Web's log. This is the contract test of the payload: the
/// real endpoint, with its real validation, is what accepts or refuses what the Web sends.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class WebErrorReportingEndToEndTests(TestApiFactory api)
{
    private const string ReportKey = "end-to-end-shared-secret-0123456789-abcdefgh";
    private const string OtherKey = "another-shared-secret-0123456789-abcdefghijkl-zz";

    private WebApplicationFactory<ApiHost::Program> ApiWithReports => api.WithErrorReportKey(ReportKey);

    private WebTestHost Web(ApiClientFaults faults, WebApplicationFactory<ApiHost::Program>? apiHost = null, string? key = ReportKey, params (string Key, string? Value)[] more)
    {
        var settings = new Dictionary<string, string?> { ["ErrorReporting:ApiKey"] = key };
        foreach (var (name, value) in more)
        {
            settings[name] = value;
        }

        return WebTestHost.Create(api, apiHost ?? ApiWithReports, new WebSettings(Config: settings), faults.Install);
    }

    private async Task<ErrorLog?> RowAsync(string code)
    {
        using var scope = ApiWithReports.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ErrorLogs.AsNoTracking().SingleOrDefaultAsync(e => e.Code == code);
    }

    private async Task<List<ErrorLog>> RowsOfAsync(string userAgentMarker)
    {
        using var scope = ApiWithReports.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ErrorLogs.AsNoTracking().Where(e => e.UserAgent == userAgentMarker).OrderBy(e => e.OccurredAt).ToListAsync();
    }

    private async Task<List<ErrorLog>> RowsMentioningInTheQueryAsync(string text)
    {
        using var scope = ApiWithReports.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ErrorLogs.AsNoTracking().Where(e => e.Query != null && e.Query.Contains(text)).ToListAsync();
    }

    private static string Marker() => $"web-e2e-{Guid.NewGuid():N}";

    /// <summary>A Web whose API client works until <see cref="Break"/> is called.</summary>
    private static ApiClientFaults Working() => ApiClientFaults.Throwing((_, _) => null);

    private static void Break(ApiClientFaults faults, string method, string message)
        => faults.Fault = (name, _) => name == method ? new InvalidOperationException(message) : null;

    // ---- the API takes the report ----

    [Fact]
    public async Task An_unhandled_exception_is_registered_under_the_code_the_person_sees()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var faults = Working();
        using var host = Web(faults);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var marker = Marker();
        faults.Fault = (method, _) => method == nameof(IApiClient.GetUsersAsync) ? new InvalidOperationException($"the user list broke {marker}") : null;

        var page = await session.GetAsync("/users?page=2", ("User-Agent", marker));

        var code = ErrorCodeAssertions.AssertUnhandledPage(page);
        var row = await RowAsync(code);
        Assert.NotNull(row);
        Assert.Equal(ErrorSource.Web, row!.Source);
        Assert.Equal(ErrorKind.Unhandled, row.Kind);
        Assert.Equal("GET", row.HttpMethod);
        Assert.Equal("/users", row.Path);
        Assert.Contains("page=2", row.Query);
        Assert.Equal(500, row.StatusCode);
        Assert.Equal(owner.UserId, row.UserId);
        Assert.Equal(owner.TenantId, row.TenantId);
        Assert.Equal("System.InvalidOperationException", row.ExceptionType);
        Assert.Equal($"the user list broke {marker}", row.ExceptionMessage);
        Assert.Contains("   at ", row.ExceptionDetails);
        Assert.Equal("Production", row.Environment);
        Assert.False(string.IsNullOrWhiteSpace(row.ServiceVersion));
        Assert.False(string.IsNullOrWhiteSpace(row.Instance));
        Assert.Equal(marker, row.UserAgent);
        Assert.False(string.IsNullOrWhiteSpace(row.RequestId));
        Assert.Matches(@"^[0-9a-f]{16}$", row.Fingerprint);
        var report = Assert.Single(host.Api.Calls, c => c.Uri.AbsolutePath == "/api/diagnostics/errors");
        Assert.Equal(HttpStatusCode.Created, report.Status);
    }

    [Fact]
    public async Task What_the_web_stores_about_a_failure_contains_no_cookie_token_or_password()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var faults = Working();
        using var host = Web(faults);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var users = await WebFlows.UsersAsync(session);
        var typed = "Typed-Pa55word-9!";
        var form = FormModel.From(users, users.FindPasswordForm()).Set("email", ApiSeed.UniqueEmail("typed")).Set("password", typed);
        faults.Fault = (method, _) => method == nameof(IApiClient.CreateUserAsync) ? new InvalidOperationException("create failed") : null;

        var page = await session.SubmitAsync(form);

        var row = await RowAsync(ErrorCodeAssertions.AssertUnhandledPage(page));
        Assert.NotNull(row);
        var everything = string.Join('\n', typeof(ErrorLog).GetProperties().Select(p => p.GetValue(row)?.ToString()));
        foreach (var secret in session.AuthCookies.Select(c => c.Value).Concat(host.Api.IssuedTokens).Append(typed).Append(ApiSeed.StrongPassword).Append(ReportKey))
        {
            Assert.DoesNotContain(secret, everything);
        }

        Assert.False(PageResponse.ContainsJwt(everything));
    }

    [Fact]
    public async Task An_unreachable_api_is_registered_by_the_web_as_upstream_unavailable()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var faults = Working();
        using var host = Web(faults);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        host.Api.Intercept = request => FakeApi.IsPath(request, "/api/diagnostics/errors")
            ? Task.FromResult<HttpResponseMessage?>(null) // the report endpoint is reachable...
            : throw new HttpRequestException("connection refused (simulated)"); // ...everything else is not

        var page = await session.GetAsync("/users");

        var code = ErrorCodeAssertions.Shown(page);
        var row = await RowAsync(code);
        Assert.NotNull(row);
        Assert.Equal(ErrorSource.Web, row!.Source);
        Assert.Equal(ErrorKind.UpstreamUnavailable, row.Kind);
        Assert.Equal("System.Net.Http.HttpRequestException", row.ExceptionType);
        Assert.Equal("/users", row.Path);
        Assert.Equal(owner.UserId, row.UserId);
        Assert.Equal(owner.TenantId, row.TenantId);
    }

    [Fact]
    public async Task An_answer_the_web_cannot_use_is_registered_as_upstream_bad_response()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var faults = Working();
        using var host = Web(faults);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        host.Api.Intercept = request => FakeApi.IsPath(request, "/api/diagnostics/errors")
            ? Task.FromResult<HttpResponseMessage?>(null)
            : FakeApi.Respond(HttpStatusCode.OK, "<html><body>not the API</body></html>", "text/html");

        var page = await session.GetAsync("/users");

        var row = await RowAsync(ErrorCodeAssertions.Shown(page));
        Assert.NotNull(row);
        Assert.Equal(ErrorKind.UpstreamBadResponse, row!.Kind);
        Assert.Equal(ErrorSource.Web, row.Source);
    }

    [Fact]
    public async Task A_failure_the_api_already_registered_is_shown_with_its_code_and_not_reported_again()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var faults = Working();
        using var host = Web(faults);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var code = Domain.Common.ErrorCode.New(DateTime.UtcNow);
        host.Api.Intercept = _ => Task.FromResult<HttpResponseMessage?>(FakeBackend.UnexpectedWithCode(code));

        var page = await session.GetAsync("/users");

        Assert.Equal(code, ErrorCodeAssertions.Shown(page));
        Assert.DoesNotContain(host.Api.Calls, c => c.Uri.AbsolutePath == "/api/diagnostics/errors");
        Assert.Null(await RowAsync(code));
    }

    [Fact]
    public async Task A_real_api_failure_is_shown_with_the_code_the_api_stored_and_the_web_registers_nothing_more()
    {
        var marker = Guid.NewGuid().ToString(); // a branch id: the API's conversation list fails for any request that filters by one
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = Web(Working(), apiHost: api.WithFailingConversationList(ReportKey));
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = await session.GetAsync($"/conversaciones?branch={marker}");

        var code = ErrorCodeAssertions.Shown(page);
        var row = await RowAsync(code);
        Assert.NotNull(row);
        Assert.Equal(ErrorSource.Api, row!.Source);
        Assert.Contains(host.Api.Calls, c => c.Status == HttpStatusCode.InternalServerError);
        Assert.DoesNotContain(host.Api.Calls, c => c.Uri.AbsolutePath == "/api/diagnostics/errors");
        var rowsOfThisFailure = await RowsMentioningInTheQueryAsync(marker); // the API's row and, if the Web wrongly wrote one, the Web's
        var only = Assert.Single(rowsOfThisFailure);
        Assert.Equal(code, only.Code);
    }

    // ---- the API refuses the report: the person still has a code ----

    [Fact]
    public async Task With_the_wrong_key_the_api_answers_401_and_the_person_still_has_a_code_that_is_logged()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var faults = Working();
        using var host = Web(faults, key: OtherKey);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        Break(faults, nameof(IApiClient.GetUsersAsync), "the user list broke");

        var page = await session.GetAsync("/users");

        var code = ErrorCodeAssertions.AssertUnhandledPage(page);
        var report = Assert.Single(host.Api.Calls, c => c.Uri.AbsolutePath == "/api/diagnostics/errors");
        Assert.Equal(HttpStatusCode.Unauthorized, report.Status);
        Assert.Null(await RowAsync(code));
        Assert.Contains(host.Logs.Records.Where(r => r.Mentions(code)), r => r.Level == Microsoft.Extensions.Logging.LogLevel.Error && r.Mentions("the user list broke"));
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Contains(OtherKey) || entry.Contains(ReportKey));
        Assert.DoesNotContain(OtherKey, page.Body);
    }

    [Fact]
    public async Task With_no_key_configured_on_the_api_the_endpoint_is_off_and_the_person_still_has_a_code_that_is_logged()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var faults = Working();
        using var host = Web(faults, apiHost: api); // the plain API: no ErrorLogging:ReportKey
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        Break(faults, nameof(IApiClient.GetUsersAsync), "the user list broke");

        var page = await session.GetAsync("/users");

        var code = ErrorCodeAssertions.AssertUnhandledPage(page);
        var report = Assert.Single(host.Api.Calls, c => c.Uri.AbsolutePath == "/api/diagnostics/errors");
        Assert.Equal(HttpStatusCode.NotFound, report.Status);
        Assert.Contains(host.Logs.Records.Where(r => r.Mentions(code)), r => r.Level == Microsoft.Extensions.Logging.LogLevel.Error && r.Mentions("the user list broke"));
    }

    // ---- storm control and many people at once, against the real endpoint ----

    [Fact]
    public async Task A_burst_is_stored_up_to_the_budget_while_every_person_sees_a_code()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var healthy = Working();
        using var host = Web(healthy, more: ("ErrorReporting:MaxPerFingerprintPerMinute", "3"));
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var marker = Marker();
        healthy.Fault = (method, _) => method == nameof(IApiClient.GetUsersAsync) ? new InvalidOperationException("burst") : null;

        var codes = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            codes.Add(ErrorCodeAssertions.AssertUnhandledPage(await session.GetAsync("/users", ("User-Agent", marker))));
        }

        Assert.Equal(8, codes.Distinct().Count());
        var rows = await RowsOfAsync(marker);
        Assert.Equal(codes.Take(3), rows.Select(r => r.Code));
        Assert.Equal(3, host.Api.Calls.Count(c => c.Uri.AbsolutePath == "/api/diagnostics/errors"));
    }

    [Fact]
    public async Task Simultaneous_failures_are_each_stored_under_their_own_code_with_their_own_request()
    {
        const int people = 20;
        var healthy = Working();
        using var host = Web(healthy, more: ("ErrorReporting:MaxPerFingerprintPerMinute", "1000"));
        var owners = new List<ApiSeed.Owner>();
        var sessions = new List<WebSession>();
        for (var i = 0; i < people; i++)
        {
            var owner = await ApiSeed.RegisterOwnerAsync(api);
            owners.Add(owner);
            sessions.Add(await WebFlows.SignedInAsync(host, owner.Email));
        }

        var branches = Enumerable.Range(0, people).Select(_ => Guid.NewGuid()).ToArray();
        healthy.Fault = (method, args) => method == nameof(IApiClient.GetConversationsAsync) ? new InvalidOperationException($"boom {((Conversia.Application.DTOs.Conversations.ConversationQuery)args[1]!).BranchId}") : null;
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
        for (var i = 0; i < people; i++)
        {
            var row = await RowAsync(codes[i]);
            Assert.NotNull(row);
            Assert.Equal($"boom {branches[i]}", row!.ExceptionMessage);
            Assert.Equal(owners[i].UserId, row.UserId);
            Assert.Equal(owners[i].TenantId, row.TenantId);
        }
    }
}
