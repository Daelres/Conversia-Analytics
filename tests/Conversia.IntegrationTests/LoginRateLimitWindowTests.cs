using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using static Conversia.IntegrationTests.RateLimitTestSupport;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Window expiry of the login rate limit, against a host with a 2-second window.
/// Waiting is driven by the <c>Retry-After</c> the API itself reports and every wait is bounded.
/// Throttling is provoked with cheap malformed logins so the budget is spent well inside one window.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class LoginRateLimitWindowTests
{
    private const int PermitLimit = 3;
    private const int WindowSeconds = 2;
    private const int MaxWaitAttempts = 3;

    private readonly CustomWebApplicationFactory _shared;
    private readonly WebApplicationFactory<Program> _limited;

    public LoginRateLimitWindowTests(CustomWebApplicationFactory factory)
    {
        _shared = factory;
        _limited = factory.WithLoginRateLimit(PermitLimit, WindowSeconds);
    }

    /// <summary>A client with a fresh address, on a host that has already paid its cold-start cost outside the window under test.</summary>
    private async Task<HttpClient> NewWarmClientAsync()
    {
        await WarmUpAsync(_limited, _shared, PermitLimit);
        return NewClient(_limited);
    }

    private static Task<int> AssertThrottledAsync(HttpResponseMessage response)
        => RateLimitTestSupport.AssertThrottledAsync(response, maxRetryAfterSeconds: WindowSeconds);

    private static async Task<int> ExhaustBudgetAndGetRetryAfterAsync(HttpClient client)
    {
        for (var i = 0; i < PermitLimit; i++)
        {
            using var allowed = await MalformedLogin(client);
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        using var throttled = await MalformedLogin(client);
        return await AssertThrottledAsync(throttled);
    }

    /// <summary>
    /// Waits the reported Retry-After and tries <paramref name="attempt"/>; if the window has not reset yet
    /// (timer granularity), waits the newly reported Retry-After again, at most <see cref="MaxWaitAttempts"/> times.
    /// </summary>
    private static async Task<HttpResponseMessage> RetryAfterWaitingAsync(
        int retryAfterSeconds, Func<Task<HttpResponseMessage>> attempt)
    {
        for (var i = 1; ; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds) + TimeSpan.FromMilliseconds(250));
            var response = await attempt();
            if (response.StatusCode != HttpStatusCode.TooManyRequests || i == MaxWaitAttempts)
            {
                return response;
            }

            retryAfterSeconds = await AssertThrottledAsync(response);
        }
    }

    [Fact]
    public async Task Login_AfterTheWindowElapses_TheSameClientCanLogInAgain()
    {
        var email = await RegisterUserAsync(_shared);
        using var client = await NewWarmClientAsync();
        var retryAfter = await ExhaustBudgetAndGetRetryAfterAsync(client);

        using var response = await RetryAfterWaitingAsync(
            retryAfter, () => client.PostAsJsonAsync("/api/auth/login", new { email, password = StrongPassword }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body?.Token));
    }

    [Fact]
    public async Task Login_AfterTheWindowElapses_ABudgetIsEnforcedAgainInTheNewWindow()
    {
        var email = await RegisterUserAsync(_shared);
        using var client = await NewWarmClientAsync();
        var retryAfter = await ExhaustBudgetAndGetRetryAfterAsync(client);

        using var firstOfNewWindow = await RetryAfterWaitingAsync(retryAfter, () => MalformedLogin(client));
        Assert.Equal(HttpStatusCode.BadRequest, firstOfNewWindow.StatusCode);
        for (var i = 1; i < PermitLimit; i++)
        {
            using var allowed = await MalformedLogin(client);
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        using var throttledAgain = await MalformedLogin(client);
        await AssertThrottledAsync(throttledAgain);
        using var correctCredentials = await client.PostAsJsonAsync("/api/auth/login", new { email, password = StrongPassword });
        await AssertThrottledAsync(correctCredentials);
    }

    [Fact]
    public async Task Login_ThrottledClientStaysThrottledUntilTheWindowElapses()
    {
        using var client = await NewWarmClientAsync();
        var retryAfter = await ExhaustBudgetAndGetRetryAfterAsync(client);

        // Immediately after being throttled (well inside the 2 s window) the client is still refused.
        using var stillThrottled = await MalformedLogin(client);
        await AssertThrottledAsync(stillThrottled);

        using var afterWindow = await RetryAfterWaitingAsync(retryAfter, () => MalformedLogin(client));
        Assert.Equal(HttpStatusCode.BadRequest, afterWindow.StatusCode);
    }
}
