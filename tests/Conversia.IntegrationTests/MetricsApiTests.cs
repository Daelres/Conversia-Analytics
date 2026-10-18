using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Application.DTOs.Metrics;
using Conversia.Application.Time;
using Conversia.Domain.Enums;
using Microsoft.Extensions.Configuration;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>The metrics endpoints over HTTP: who may call them, what they refuse, the shape of the answer and the default period (which uses the real clock, so the data is relative to now).</summary>
[Collection(nameof(IntegrationTestCollection))]
public class MetricsApiTests
{
    private static readonly string[] Endpoints = ["summary", "conversion", "first-response", "loss-reasons", "volume", "alerts"];

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MetricsApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public static TheoryData<string> AllEndpoints => new(Endpoints);

    private static string Url(string endpoint, string query = "") => query.Length == 0 ? $"/api/metrics/{endpoint}" : $"/api/metrics/{endpoint}?{query}";

    // ---- Permissions -------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task Anonymous_Is401_WhileTheOwnerGets200(string endpoint)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "metrics");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(Url(endpoint))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Owner.Client.GetAsync(Url(endpoint))).StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task WithoutMetricsView_Is403_NoMatterWhatElseTheMemberCan(string endpoint)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "metrics");
        using var none = await tenant.AddMemberAsync("none");
        using var others = await tenant.AddMemberAsync(
            "others", Permission.ConversationsView, Permission.ConversationsManage, Permission.OrganizationView, Permission.OrganizationManage, Permission.UsersView, Permission.ProductsView);
        using var viewer = await tenant.AddMemberAsync("viewer", Permission.MetricsView);

        Assert.Equal(HttpStatusCode.Forbidden, (await none.Client.GetAsync(Url(endpoint))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await others.Client.GetAsync(Url(endpoint))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.Client.GetAsync(Url(endpoint))).StatusCode);
    }

    [Fact]
    public async Task MetricsView_DoesNotOpenTheConversations()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "metrics");
        using var viewer = await tenant.AddMemberAsync("viewer", Permission.MetricsView);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.GetAsync("/api/conversations")).StatusCode);
    }

    // ---- Validation --------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllEndpoints))]
    public async Task ARangeLongerThan366Days_Is400WithASpanishProblem(string endpoint)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "metrics");

        var tooLong = await tenant.Owner.Client.GetAsync(Url(endpoint, "from=2025-01-01&to=2026-01-03")); // 367 days
        var longest = await tenant.Owner.Client.GetAsync(Url(endpoint, "from=2025-01-01&to=2026-01-02")); // 366 days

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("application/problem+json", tooLong.Content.Headers.ContentType?.MediaType);
        var problem = await tooLong.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>();
        Assert.Contains("El rango de fechas no puede superar los 366 días.", problem!.Errors.Values.SelectMany(v => v));
        Assert.Equal(HttpStatusCode.OK, longest.StatusCode);
    }

    [Theory]
    [InlineData("from=2026-10-02&to=2026-10-01", "El campo Desde debe ser anterior al campo Hasta.")]
    [InlineData("from=2026-10-01&to=2026-10-01", "El campo Desde debe ser anterior al campo Hasta.")]
    [InlineData("advisorId=11111111-1111-1111-1111-111111111111&unassigned=true", "No se puede filtrar por un asesor y por conversaciones sin asesor a la vez.")]
    public async Task AnInconsistentQuery_Is400(string query, string message)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "metrics");

        var response = await tenant.Owner.Client.GetAsync(Url("conversion", query));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>();
        Assert.Contains(message, problem!.Errors.Values.SelectMany(v => v));
    }

    [Theory]
    [InlineData("from=not-a-date")]
    [InlineData("to=2026-13-45")]
    [InlineData("source=Radio")]
    [InlineData("source=1")]
    [InlineData("branchId=not-a-guid")]
    [InlineData("unassigned=maybe")]
    public async Task ABadValue_Is400ProblemJson(string query)
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "metrics");

        var response = await tenant.Owner.Client.GetAsync(Url("summary", query));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- Shape and defaults ------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAnswers_AreCamelCaseJson_WithThePeriodEchoed_AndRatesAsNumbers()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        await t.SeedAsync(_factory.Services, false, DateTime.UtcNow.AddHours(-2), [In(0), Out(300)], stage: OpportunityStage.Won);

        var json = await t.Owner.GetStringAsync(Url("conversion"));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(30, root.GetProperty("period").GetProperty("days").GetInt32());
        Assert.Equal("America/Bogota", root.GetProperty("period").GetProperty("timeZone").GetString());
        Assert.Equal(1, root.GetProperty("totals").GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("totals").GetProperty("conversionRate").ValueKind);
        Assert.Equal("Centro", root.GetProperty("byBranch")[0].GetProperty("name").GetString());
        Assert.Equal("Organic", root.GetProperty("bySource")[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("byAdvisor")[0].GetProperty("id").ValueKind); // the unassigned bucket
        Assert.Equal("Sin asesor", root.GetProperty("byAdvisor")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task WithoutDates_TheLast30DaysIncludingToday_AreUsed()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        var zone = new TenantTimeZone("America/Bogota");
        await t.SeedAsync(_factory.Services, false, DateTime.UtcNow.AddHours(-3), [In(0), Out(60)]); // today or yesterday, but within 30 days
        await t.SeedAsync(_factory.Services, false, DateTime.UtcNow.AddDays(-28), [In(0), Out(60)]); // inside
        await t.SeedAsync(_factory.Services, false, DateTime.UtcNow.AddDays(-31), [In(0), Out(60)]); // outside whatever the hour

        var before = zone.LocalDate(DateTime.UtcNow);
        var response = await t.Owner.GetFromJsonAsync<ConversionResponse>(Url("conversion"));
        var after = zone.LocalDate(DateTime.UtcNow);

        Assert.Equal(2, response!.Totals.Total);
        Assert.Equal(30, response.Period.Days);
        Assert.Contains(response.Period.To, new[] { before.AddDays(1), after.AddDays(1) }); // the period ends tomorrow (exclusive), however the run straddles midnight
    }

    [Fact]
    public async Task EveryEndpoint_AnswersAnEmptyTenant()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "metrics");

        foreach (var endpoint in Endpoints)
        {
            var response = await tenant.Owner.Client.GetAsync(Url(endpoint));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        var volume = await tenant.Owner.Client.GetFromJsonAsync<VolumeResponse>(Url("volume"));
        Assert.Equal((30, 168, 0), (volume!.ByDay.Count, volume.ByHourOfWeek.Count, volume.Conversations));
    }

    [Fact]
    public async Task TheAlertsEndpoint_ListsAnUnansweredConversation_WithAGeneratedAtAndWithoutCustomerData()
    {
        using var t = await MetricsTenant.StartAsync(_factory, _client);
        await t.SeedAsync(_factory.Services, false, DateTime.UtcNow.AddHours(-3), [In(0, "Hola soy Marcela, mi cédula es 52000111")]);

        var body = await t.Owner.GetStringAsync(Url("alerts"));
        var alerts = JsonSerializer.Deserialize<AlertsResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var alert = Assert.Single(alerts.Alerts);
        Assert.Equal(AlertCodes.UnansweredOverSla, alert.Code);
        Assert.Equal(AlertSeverities.Critical, alert.Severity); // three hours is more than four times the 30 minutes
        Assert.StartsWith("Hay 1 conversación sin responder hace más de 30 minutos", alert.Message);
        Assert.Single(alerts.Unanswered.Items);
        Assert.DoesNotContain("Marcela", body);
        Assert.DoesNotContain("52000111", body);
        Assert.DoesNotContain("Cliente", body); // not even the contact's name
    }

    // ---- Tenant isolation over HTTP ----------------------------------------------------------------------------------

    [Fact]
    public async Task AnotherTenantsCaller_SeesNoneOfThisTenantsNumbers()
    {
        using var a = await MetricsTenant.StartAsync(_factory, _client);
        using var b = await MetricsTenant.StartAsync(_factory, _client);
        await a.SeedAsync(_factory.Services, false, DateTime.UtcNow.AddHours(-3), [In(0), Out(60)], stage: OpportunityStage.Won);
        await a.SeedAsync(_factory.Services, false, DateTime.UtcNow.AddHours(-2), [In(0)]);

        var ofA = await a.Owner.GetFromJsonAsync<ConversionResponse>(Url("conversion"));
        var ofB = await b.Owner.GetFromJsonAsync<ConversionResponse>(Url("conversion"));
        var alertsOfB = await b.Owner.GetFromJsonAsync<AlertsResponse>(Url("alerts"));
        var filteredByA = await b.Owner.GetFromJsonAsync<ConversionResponse>(Url("conversion", $"branchId={a.Centro.Id}"));

        Assert.Equal(2, ofA!.Totals.Total);
        Assert.Equal(0, ofB!.Totals.Total);
        Assert.Empty(ofB.ByBranch);
        Assert.Equal(0, alertsOfB!.Unanswered.Total);
        Assert.Equal(0, filteredByA!.Totals.Total);
    }

    // ---- Configuration -----------------------------------------------------------------------------------------------

    private static IConfiguration Config(params (string, string)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

    [Fact]
    public void TheDefaults_AreTheDocumentedOnes_AndBadValuesStopTheApiAtStartupNamingTheKey()
    {
        var defaults = Conversia.API.Configuration.MetricsServiceCollectionExtensions.GetValidatedOptions(Config());
        Assert.Equal((30, 7, 5, 20, 10, 15), (defaults.FirstResponseSlaMinutes, defaults.ConversionMaturityDays, defaults.TopLossReasons, defaults.MinSampleSize, defaults.ConversionDropAlertPoints, defaults.BranchBelowAveragePoints));

        var custom = Conversia.API.Configuration.MetricsServiceCollectionExtensions.GetValidatedOptions(Config(("Metrics:FirstResponseSlaMinutes", "15"), ("Metrics:MinSampleSize", "50")));
        Assert.Equal((15, 50), (custom.FirstResponseSlaMinutes, custom.MinSampleSize));

        foreach (var (key, value) in new[]
                 {
                     ("Metrics:FirstResponseSlaMinutes", "0"), ("Metrics:FirstResponseSlaMinutes", "1441"), ("Metrics:ConversionMaturityDays", "0"), ("Metrics:TopLossReasons", "21"),
                     ("Metrics:MinSampleSize", "0"), ("Metrics:ConversionDropAlertPoints", "101"), ("Metrics:BranchBelowAveragePoints", "many"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Conversia.API.Configuration.MetricsServiceCollectionExtensions.GetValidatedOptions(Config((key, value))));
            Assert.Contains(key, ex.Message);
        }
    }
}
