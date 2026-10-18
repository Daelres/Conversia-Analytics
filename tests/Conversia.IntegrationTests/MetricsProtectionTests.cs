using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Npgsql;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The protections of the heavy endpoints (<c>/api/metrics/*</c> and <c>GET /api/conversations</c>): a request budget per user, a time limit per
/// query, and dates that are refused instead of overflowing.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class MetricsProtectionTests
{
    private const int Budget = 3;

    private static readonly string[] MetricsEndpoints = ["summary", "conversion", "first-response", "loss-reasons", "volume", "alerts"];

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MetricsProtectionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private WebApplicationFactory<Program> Limited()
        => _factory.GetOrAddHost("metrics-rate-limit", () => _factory.WithWebHostBuilder(builder => builder.UseSetting("Metrics:RateLimitPerMinute", Budget.ToString())));

    private static HttpClient As(WebApplicationFactory<Program> host, string token, string? ip = null)
    {
        var client = RateLimitTestSupport.ClientFrom(host, ip);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ---- The request budget --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMetricsEndpointsAndTheConversationList_ShareOneBudgetPerUser_ThenAnswer429WithRetryAfter()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "throttle");
        using var client = As(Limited(), tenant.Owner.Token, TestClientIp.Next());

        // Three requests across different endpoints spend the same budget...
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/metrics/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/metrics/volume")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/conversations")).StatusCode);

        // ...and the fourth, whichever it is, is refused.
        foreach (var url in new[] { "/api/metrics/alerts", "/api/conversations", "/api/metrics/conversion?from=2026-10-01&to=2026-11-01" })
        {
            var response = await client.GetAsync(url);

            await AssertThrottledAsync(response);
        }
    }

    [Fact]
    public async Task TheBudgetBelongsToTheUser_NotToTheAddressOrToTheTenant()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "throttle");
        using var colleague = await tenant.AddMemberAsync("colleague", Permission.MetricsView);
        var host = Limited();
        var address = TestClientIp.Next();
        using var owner = As(host, tenant.Owner.Token, address);
        for (var i = 0; i < Budget; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/metrics/summary")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await owner.GetAsync("/api/metrics/summary")).StatusCode);

        // The same user from another address is still the same user. A colleague of the same tenant, from the very same address, is not.
        using var ownerElsewhere = As(host, tenant.Owner.Token, TestClientIp.Next());
        using var colleagueSameAddress = As(host, colleague.Token, address);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ownerElsewhere.GetAsync("/api/metrics/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await colleagueSameAddress.GetAsync("/api/metrics/summary")).StatusCode);
    }

    [Fact]
    public async Task OtherEndpoints_AreNotThrottledByTheHeavyQueryBudget()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "throttle");
        using var client = As(Limited(), tenant.Owner.Token, TestClientIp.Next());
        for (var i = 0; i < Budget; i++)
        {
            await client.GetAsync("/api/metrics/summary");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/metrics/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/branches")).StatusCode);
    }

    [Fact]
    public async Task AnAnonymousCaller_IsRefusedAs401_NotLeftToSpendAUsersBudget()
    {
        using var anonymous = RateLimitTestSupport.ClientFrom(Limited(), TestClientIp.Next());

        var response = await anonymous.GetAsync("/api/metrics/summary");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task AssertThrottledAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.InRange(int.Parse(Assert.Single(values!)), 1, 60);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<ProblemDetails>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(429, problem.Status);
        Assert.Equal("Demasiadas solicitudes", problem.Title);
        Assert.Equal("Demasiadas consultas en poco tiempo. Espera unos segundos e inténtalo de nuevo.", problem.Detail);
        Assert.DoesNotContain("   at ", raw);
    }

    // ---- The time limit of a query -------------------------------------------------------------------------------------

    [Fact]
    public async Task AQueryThatTakesTooLong_Is503WithASpanishProblem_NoSqlAndNoStack_AndTheNextOneWorks()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "timeout");
        var capture = new CapturingLoggerProvider();
        using var host = WebhookHost(_factory, new() { ["Metrics:QueryTimeoutSeconds"] = "1" }, configureLogging: logging => logging.AddProvider(capture));
        using var client = As(host, tenant.Owner.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/metrics/conversion")).StatusCode); // warms the host up with a free table

        HttpResponseMessage slow;
        await using (var connection = new NpgsqlConnection(_factory.ServerConnectionString))
        {
            await connection.OpenAsync();
            await using var holder = await connection.BeginTransactionAsync();
            // The table is locked for everybody: the aggregation waits for it and runs out of time, as a statement stuck behind a long one would.
            await using (var lockTable = new NpgsqlCommand("LOCK TABLE \"Conversations\" IN ACCESS EXCLUSIVE MODE", connection, holder))
            {
                await lockTable.ExecuteNonQueryAsync();
            }

            slow = await client.GetAsync("/api/metrics/conversion");
            await holder.RollbackAsync();
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, slow.StatusCode);
        Assert.Equal("application/problem+json", slow.Content.Headers.ContentType?.MediaType);
        var raw = await slow.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<ProblemDetails>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal((503, "Consulta demasiado lenta"), (problem.Status, problem.Title));
        Assert.Equal("La consulta tardó demasiado en responder. Acota el rango de fechas o los filtros e inténtalo de nuevo.", problem.Detail);
        Assert.DoesNotContain("SELECT", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Conversations", raw);
        Assert.DoesNotContain("   at ", raw);
        Assert.False(slow.Headers.Contains("X-Error-Id")); // expected under load: not registered as an unhandled error

        // The log says that it happened, and nothing of the statement, its parameters or a stack.
        var warning = Assert.Single(capture.Entries, e => e.Text.Contains("exceeded its time limit"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.All(capture.Entries, e =>
        {
            Assert.DoesNotContain("SELECT", e.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PostgresException", e.Text);
            Assert.DoesNotContain("   at ", e.Text);
        });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/metrics/conversion")).StatusCode);
    }

    // ---- Dates that are refused ----------------------------------------------------------------------------------------

    public static TheoryData<string> ExtremeQueries() =>
    [
        "from=9999-12-31",
        "to=0001-01-05",
        "from=9999-12-31&to=9999-12-31",
        "from=0001-01-01&to=9999-12-31",
        "from=2014-12-31&to=2015-01-31",
        "from=2026-10-01&to=2099-01-01",
        "to=2014-12-31",
    ];

    [Theory]
    [MemberData(nameof(ExtremeQueries))]
    public async Task AnExtremeDate_IsA400WithASpanishMessage_NeverA500_OnEveryMetricsEndpoint(string query)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "dates");

        foreach (var endpoint in MetricsEndpoints)
        {
            var response = await tenant.Owner.Client.GetAsync($"/api/metrics/{endpoint}?{query}");

            await AssertBadDateAsync(response);
        }
    }

    [Theory]
    [MemberData(nameof(ExtremeQueries))]
    public async Task AnExtremeDate_IsA400_AlsoOnTheConversationList(string query)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "dates");

        var response = await tenant.Owner.Client.GetAsync($"/api/conversations?{query}");

        await AssertBadDateAsync(response);
    }

    private static async Task AssertBadDateAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        var messages = problem!.Errors.Values.SelectMany(v => v).ToList();
        Assert.NotEmpty(messages);
        Assert.All(messages, m => Assert.Matches(@"^El campo (Desde|Hasta) (debe ser una fecha entre el 2015-01-01 y el \d{4}-\d{2}-\d{2}|debe ser anterior al campo Hasta)\.$|^El rango de fechas no puede superar", m));
    }

    [Fact]
    public async Task TheEdgesOfTheWindow_AreAccepted()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "dates");
        var inAYear = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(360);

        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.Client.GetAsync("/api/metrics/summary?from=2015-01-01&to=2015-02-01")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.Client.GetAsync($"/api/metrics/alerts?to={inAYear:yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.Client.GetAsync($"/api/conversations?from={inAYear:yyyy-MM-dd}")).StatusCode);
    }
}
