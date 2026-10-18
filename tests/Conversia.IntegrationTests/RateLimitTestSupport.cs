using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>Helpers shared by the login rate limit suites (<see cref="LoginRateLimitTests"/>, <see cref="LoginRateLimitWindowTests"/>).</summary>
internal static class RateLimitTestSupport
{
    private static readonly ConcurrentDictionary<WebApplicationFactory<Program>, Lazy<Task>> WarmedHosts = new();

    /// <summary>A client of <paramref name="host"/> that appears to come from <paramref name="ip"/> (or from no address when null).</summary>
    public static HttpClient ClientFrom(WebApplicationFactory<Program> host, string? ip)
    {
        var client = host.CreateClient();
        if (ip is not null)
        {
            client.DefaultRequestHeaders.Add(TestClientIp.HeaderName, ip);
        }

        return client;
    }

    /// <summary>A client of <paramref name="host"/> with its own never-used address, i.e. its own rate-limit partition.</summary>
    public static HttpClient NewClient(WebApplicationFactory<Program> host) => ClientFrom(host, TestClientIp.Next());

    /// <summary>Registers an owner through the unthrottled shared host so setup never spends rate-limit budget.</summary>
    public static async Task<string> RegisterUserAsync(CustomWebApplicationFactory shared)
    {
        using var setupClient = shared.CreateClient();
        return (await RegisterTenantSessionAsync(setupClient, "ratelimit")).OwnerEmail;
    }

    public static Task<HttpResponseMessage> LoginAs(HttpClient client, string email, string password = StrongPassword)
        => client.PostAsJsonAsync("/api/auth/login", new { email, password });

    /// <summary>A body that fails to parse: answered 400 without ever reaching the database.</summary>
    public static Task<HttpResponseMessage> MalformedLogin(HttpClient client)
        => client.PostAsync("/api/auth/login", new StringContent("{not json", Encoding.UTF8, "application/json"));

    /// <summary>
    /// Pays the cold-start cost of a freshly derived host (JIT, pipeline and DB connection startup)
    /// once, from throwaway addresses and a throwaway account, so tests with a short window
    /// do not spend part of it on warm-up. Safe to call from every test; only the first call does work.
    /// </summary>
    public static Task WarmUpAsync(WebApplicationFactory<Program> host, CustomWebApplicationFactory shared, int permitLimit)
        => WarmedHosts.GetOrAdd(host, _ => new Lazy<Task>(() => WarmUpCoreAsync(host, shared, permitLimit))).Value;

    private static async Task WarmUpCoreAsync(WebApplicationFactory<Program> host, CustomWebApplicationFactory shared, int permitLimit)
    {
        var email = await RegisterUserAsync(shared);
        using var client = NewClient(host);

        // A real login (database path), then malformed ones, then the throttled 429 path.
        using (await LoginAs(client, email))
        {
        }

        for (var i = 1; i < permitLimit; i++)
        {
            using (await MalformedLogin(client))
            {
            }
        }

        using (await MalformedLogin(client))
        {
        }
    }

    /// <summary>
    /// Asserts the full 429 contract and returns the Retry-After seconds:
    /// problem+json, a ProblemDetails with status 429, whole-second Retry-After in
    /// [1, <paramref name="maxRetryAfterSeconds"/>] (one window), no stack trace.
    /// </summary>
    public static async Task<int> AssertThrottledAsync(HttpResponseMessage response, int maxRetryAfterSeconds)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfterValues), "429 must carry Retry-After.");
        var retryAfterRaw = Assert.Single(retryAfterValues!);
        Assert.Matches(new Regex(@"^\d+$"), retryAfterRaw);
        var retryAfter = int.Parse(retryAfterRaw);
        Assert.InRange(retryAfter, 1, maxRetryAfterSeconds);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<ProblemDetails>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(problem);
        Assert.Equal(429, problem!.Status);
        Assert.Equal(ApiMessages.RateLimited, problem.Detail);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        Assert.Empty(Conversia.TestSupport.EnglishLeaks.Find(problem.Title));
        Assert.DoesNotContain("   at ", raw);
        Assert.DoesNotContain("Exception", raw);
        Assert.DoesNotContain("stacktrace", raw, StringComparison.OrdinalIgnoreCase);

        return retryAfter;
    }
}
