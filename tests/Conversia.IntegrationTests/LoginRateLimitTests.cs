using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using static Conversia.IntegrationTests.RateLimitTestSupport;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Brute-force protection on <c>POST /api/auth/login</c>: a fixed window per client IP.
/// Runs against a host (same SQL container as the other suites) limited to
/// <see cref="PermitLimit"/> requests per long window, so the window can never
/// elapse mid-test; the window-expiry behavior lives in <see cref="LoginRateLimitWindowTests"/>.
/// Every test uses its own never-reused client address, i.e. its own rate-limit partition.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class LoginRateLimitTests
{
    private const int PermitLimit = 3;
    private const int WindowSeconds = 60;

    private readonly CustomWebApplicationFactory _shared;
    private readonly WebApplicationFactory<Program> _limited;

    public LoginRateLimitTests(CustomWebApplicationFactory factory)
    {
        _shared = factory;
        _limited = factory.WithLoginRateLimit(PermitLimit, WindowSeconds);
    }

    private HttpClient ClientFrom(string? ip) => RateLimitTestSupport.ClientFrom(_limited, ip);

    private Task<string> RegisterUserAsync() => RateLimitTestSupport.RegisterUserAsync(_shared);

    /// <summary>Spends the whole budget with cheap malformed logins (400s that never touch the database).</summary>
    private static async Task ExhaustBudgetAsync(HttpClient client)
    {
        for (var i = 0; i < PermitLimit; i++)
        {
            using var response = await MalformedLogin(client);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    private static Task<int> AssertThrottledAsync(HttpResponseMessage response)
        => RateLimitTestSupport.AssertThrottledAsync(response, maxRetryAfterSeconds: WindowSeconds);

    [Fact]
    public async Task Login_UpToThePermitLimit_ReachesLoginLogic_AndTheNextRequestIs429()
    {
        var email = await RegisterUserAsync();
        using var client = ClientFrom(TestClientIp.Next());

        for (var i = 0; i < PermitLimit; i++)
        {
            using var allowed = await LoginAs(client, email);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            var body = await allowed.Content.ReadFromJsonAsync<AuthResponse>();
            Assert.False(string.IsNullOrWhiteSpace(body?.Token));
        }

        using var throttled = await LoginAs(client, email);

        await AssertThrottledAsync(throttled);
    }

    [Fact]
    public async Task Login_Throttled_IsProblemJsonWithWholeSecondRetryAfterAndNoStackTrace()
    {
        var email = await RegisterUserAsync();
        using var client = ClientFrom(TestClientIp.Next());
        for (var i = 0; i < PermitLimit; i++)
        {
            using var wrong = await LoginAs(client, email, "totally-wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var throttled = await LoginAs(client, email, "totally-wrong");

        var retryAfter = await AssertThrottledAsync(throttled);
        // Fixed window: the client is never told to wait longer than one window.
        Assert.InRange(retryAfter, 1, WindowSeconds);
    }

    [Fact]
    public async Task Login_ThrottledRequest_KeepsBeingThrottledOnEveryFurtherAttemptInTheWindow()
    {
        using var client = ClientFrom(TestClientIp.Next());
        await ExhaustBudgetAsync(client);

        for (var i = 0; i < 5; i++)
        {
            using var throttled = await MalformedLogin(client);
            await AssertThrottledAsync(throttled);
        }
    }

    [Theory]
    [InlineData("wrong,wrong,wrong")]
    [InlineData("ok,ok,ok")]
    [InlineData("wrong,ok,wrong")]
    [InlineData("unknown,ok,wrong")]
    public async Task Login_SuccessfulAndFailedAttemptsBothCountTowardTheLimit(string attempts)
    {
        var email = await RegisterUserAsync();
        using var client = ClientFrom(TestClientIp.Next());
        var expectedStatuses = new Dictionary<string, HttpStatusCode>
        {
            ["ok"] = HttpStatusCode.OK,
            ["wrong"] = HttpStatusCode.Unauthorized,
            ["unknown"] = HttpStatusCode.Unauthorized,
        };

        foreach (var attempt in attempts.Split(','))
        {
            using var response = attempt switch
            {
                "ok" => await LoginAs(client, email),
                "wrong" => await LoginAs(client, email, "totally-wrong"),
                _ => await LoginAs(client, UniqueEmail("nobody"), "whatever"),
            };
            Assert.Equal(expectedStatuses[attempt], response.StatusCode);
        }

        using var throttled = await LoginAs(client, email);

        await AssertThrottledAsync(throttled);
    }

    [Fact]
    public async Task Login_MalformedRequestsAlsoCountTowardTheLimit()
    {
        var email = await RegisterUserAsync();
        using var client = ClientFrom(TestClientIp.Next());
        await ExhaustBudgetAsync(client);

        using var response = await LoginAs(client, email);

        await AssertThrottledAsync(response);
    }

    [Fact]
    public async Task Login_CorrectCredentialsAfterTheLimitIsExhausted_Is429AndIssuesNoToken()
    {
        var email = await RegisterUserAsync();
        var blockedIp = TestClientIp.Next();
        using var blocked = ClientFrom(blockedIp);
        for (var i = 0; i < PermitLimit; i++)
        {
            using var wrong = await LoginAs(blocked, email, "totally-wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var response = await LoginAs(blocked, email);

        await AssertThrottledAsync(response);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("eyJ", raw); // no JWT anywhere in the body
        Assert.True(IssuesNoToken(raw), "The 429 body must not deserialize into an AuthResponse carrying a token.");

        // The same credentials do work from an unthrottled address: it is the limit, not the account, that refused.
        using var otherClient = ClientFrom(TestClientIp.Next());
        using var fromElsewhere = await LoginAs(otherClient, email);
        Assert.Equal(HttpStatusCode.OK, fromElsewhere.StatusCode);
    }

    private static bool IssuesNoToken(string body)
    {
        try
        {
            var auth = System.Text.Json.JsonSerializer.Deserialize<AuthResponse>(
                body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            return string.IsNullOrWhiteSpace(auth?.Token);
        }
        catch (System.Text.Json.JsonException)
        {
            return true;
        }
    }

    [Fact]
    public async Task Login_RejectedRequests_NeverTouchTheDatabase()
    {
        var email = await RegisterUserAsync();
        using var client = ClientFrom(TestClientIp.Next());
        var beforeAllowed = _shared.DbCommands.Count;
        for (var i = 0; i < PermitLimit; i++)
        {
            using var wrong = await LoginAs(client, email, "totally-wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var afterAllowed = _shared.DbCommands.Count;
        Assert.True(afterAllowed > beforeAllowed, "Sanity check: allowed logins must hit the database, or this test proves nothing.");

        for (var i = 0; i < 5; i++)
        {
            using var rejected = await LoginAs(client, i % 2 == 0 ? email : UniqueEmail("nobody"));
            await AssertThrottledAsync(rejected);
        }

        Assert.Equal(afterAllowed, _shared.DbCommands.Count);
    }

    [Fact]
    public async Task Login_DifferentClientIps_HaveIndependentBudgets()
    {
        var email = await RegisterUserAsync();
        using var exhausted = ClientFrom(TestClientIp.Next());
        using var fresh = ClientFrom(TestClientIp.Next());
        await ExhaustBudgetAsync(exhausted);

        for (var i = 0; i < PermitLimit; i++)
        {
            using var fromFresh = await LoginAs(fresh, email);
            Assert.Equal(HttpStatusCode.OK, fromFresh.StatusCode);
        }

        // Each client is throttled only by its own traffic, in both directions.
        using var freshThrottled = await LoginAs(fresh, email);
        await AssertThrottledAsync(freshThrottled);
        using var exhaustedStillThrottled = await LoginAs(exhausted, email);
        await AssertThrottledAsync(exhaustedStillThrottled);
    }

    [Fact]
    public async Task Login_ExhaustingOneIp_NeverAffectsAnotherIpsFirstRequest()
    {
        var email = await RegisterUserAsync();
        using var exhausted = ClientFrom(TestClientIp.Next());
        await ExhaustBudgetAsync(exhausted);
        using var throttled = await MalformedLogin(exhausted);
        await AssertThrottledAsync(throttled);

        using var newcomer = ClientFrom(TestClientIp.Next());
        using var response = await LoginAs(newcomer, email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_ClientsOfDifferentAddressFamilies_HaveIndependentBudgets()
    {
        using var v4 = ClientFrom("192.0.2.1");
        using var v6 = ClientFrom("2001:db8::1");
        await ExhaustBudgetAsync(v4);

        using var v6First = await MalformedLogin(v6);
        Assert.Equal(HttpStatusCode.BadRequest, v6First.StatusCode);
        using var v4Throttled = await MalformedLogin(v4);
        await AssertThrottledAsync(v4Throttled);
    }

    [Fact]
    public async Task OtherEndpoints_AreUnaffectedByAnExhaustedLoginBudget()
    {
        var ip = TestClientIp.Next();
        using var client = ClientFrom(ip);
        await ExhaustBudgetAsync(client);
        using var throttled = await MalformedLogin(client);
        await AssertThrottledAsync(throttled);

        var session = await RegisterTenantSessionAsync(client, "same-ip-after-login-exhaustion");
        using var authorized = ClientFrom(ip);
        authorized.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.OwnerToken);
        using var listUsers = await authorized.GetAsync("/api/users");
        using var createUser = await CreateUserAsync(authorized, UniqueEmail("member"));

        Assert.Equal(HttpStatusCode.OK, listUsers.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createUser.StatusCode);
    }

    [Fact]
    public async Task OtherEndpoints_AreNotRateLimitedByThisFeature()
    {
        // More calls than the login budget, all from one address, none of them logins.
        var ip = TestClientIp.Next();
        using var client = ClientFrom(ip);
        TenantSession? session = null;
        for (var i = 0; i < PermitLimit + 2; i++)
        {
            session = await RegisterTenantSessionAsync(client, "unlimited-registration");
        }

        using var authorized = ClientFrom(ip);
        authorized.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session!.OwnerToken);
        for (var i = 0; i < PermitLimit + 2; i++)
        {
            using var listUsers = await authorized.GetAsync("/api/users");
            Assert.Equal(HttpStatusCode.OK, listUsers.StatusCode);
        }

        // ... and none of that traffic spent login budget.
        for (var i = 0; i < PermitLimit; i++)
        {
            using var login = await LoginAs(client, session!.OwnerEmail);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
    }

    [Fact]
    public async Task Login_ConcurrentRequestsFromOneIp_LetExactlyPermitLimitThrough()
    {
        const int total = 40;
        var email = await RegisterUserAsync();
        using var client = ClientFrom(TestClientIp.Next());

        var statuses = await RunInParallelAsync(
            Enumerable.Range(0, total).Select(_ => (Func<Task<HttpResponseMessage>>)(() => LoginAs(client, email))));

        Assert.DoesNotContain(null, statuses);
        Assert.DoesNotContain(statuses, s => (int)s! >= 500);
        Assert.Equal(PermitLimit, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(total - PermitLimit, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Login_ConcurrentMixOfCorrectAndWrongCredentials_LetsExactlyPermitLimitThroughAndNeverFails()
    {
        const int total = 40;
        var email = await RegisterUserAsync();
        using var client = ClientFrom(TestClientIp.Next());

        var statuses = await RunInParallelAsync(
            Enumerable.Range(0, total).Select(i => (Func<Task<HttpResponseMessage>>)(
                () => LoginAs(client, email, i % 2 == 0 ? StrongPassword : "totally-wrong"))));

        Assert.DoesNotContain(null, statuses);
        Assert.All(statuses, s => Assert.Contains(
            s, new HttpStatusCode?[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests }));
        Assert.Equal(PermitLimit, statuses.Count(s => s is HttpStatusCode.OK or HttpStatusCode.Unauthorized));
        Assert.Equal(total - PermitLimit, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Login_ConcurrentRequestsFromTwoIps_GiveEachIpItsOwnFullBudget()
    {
        const int perIp = 20;
        var email = await RegisterUserAsync();
        using var clientA = ClientFrom(TestClientIp.Next());
        using var clientB = ClientFrom(TestClientIp.Next());

        var calls = Enumerable.Range(0, perIp).Select(_ => (Func<Task<HttpResponseMessage>>)(() => LoginAs(clientA, email)))
            .Concat(Enumerable.Range(0, perIp).Select(_ => (Func<Task<HttpResponseMessage>>)(() => LoginAs(clientB, email))))
            .ToList();
        var statuses = await RunInParallelAsync(calls);

        Assert.DoesNotContain(null, statuses);
        foreach (var perClient in new[] { statuses.Take(perIp), statuses.Skip(perIp) })
        {
            var results = perClient.ToList();
            Assert.Equal(PermitLimit, results.Count(s => s == HttpStatusCode.OK));
            Assert.Equal(perIp - PermitLimit, results.Count(s => s == HttpStatusCode.TooManyRequests));
        }
    }

    [Fact]
    public async Task Login_ConcurrentThrottledRequests_AllCarryTheFullProblemContract()
    {
        using var client = ClientFrom(TestClientIp.Next());
        await ExhaustBudgetAsync(client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => MalformedLogin(client)));

        foreach (var response in responses)
        {
            using (response)
            {
                await AssertThrottledAsync(response);
            }
        }
    }

    [Fact]
    public async Task Login_WithoutAResolvableClientIp_StillGetsOnlyControlledResponses()
    {
        // The requirement does not say how a request with no remote address is limited,
        // only that it must never crash: no 5xx and no transport failure, whatever the policy.
        using var client = ClientFrom(ip: null);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < PermitLimit + 3; i++)
        {
            using var response = await MalformedLogin(client);
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses, s => Assert.Contains(s, new[] { HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests }));
    }
}
