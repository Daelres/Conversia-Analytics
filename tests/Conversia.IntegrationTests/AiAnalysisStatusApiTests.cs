using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Metrics;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// What the company sees of the AI schedule: when its AI fields were last updated (the dashboard and the conversation), never how or by
/// what (docs/CLASSIFICATION.md, "When the AI reads").
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class AiAnalysisStatusApiTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AiAnalysisStatusApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task TheDashboardStatus_SaysWhetherTheAiIsOn_AndWhenItsFieldsWereLastUpdated()
    {
        using var f = await StartChannelAsync(_factory, _client);
        var older = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddDays(-2), [In(0, "Hola")]);
        var newer = await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddDays(-1), [In(0, "Hola")]);

        var off = (await f.Tenant.Owner.Client.GetFromJsonAsync<AiAnalysisStatusResponse>("/api/tenant/ai-analysis"))!;
        Assert.Equal((false, (DateTime?)null), (off.Enabled, off.LastUpdatedAt));

        var at = new DateTime(2026, 11, 12, 6, 30, 0, DateTimeKind.Utc);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE \"Tenants\" SET \"AiEnabled\" = true WHERE \"Id\" = {f.TenantId}");
            await db.Database.ExecuteSqlAsync($"UPDATE \"Conversations\" SET \"LlmAnalyzedAt\" = {at.AddHours(-24)} WHERE \"Id\" = {older.ConversationId}");
            await db.Database.ExecuteSqlAsync($"UPDATE \"Conversations\" SET \"LlmAnalyzedAt\" = {at} WHERE \"Id\" = {newer.ConversationId}");
        }

        var on = (await f.Tenant.Owner.Client.GetFromJsonAsync<AiAnalysisStatusResponse>("/api/tenant/ai-analysis"))!;
        Assert.Equal((true, (DateTime?)at), (on.Enabled, on.LastUpdatedAt));

        var detail = (await f.Tenant.Owner.Client.GetFromJsonAsync<ConversationDetailResponse>($"/api/conversations/{older.ConversationId}"))!;
        Assert.Equal((DateTime?)at.AddHours(-24), detail.AiAnalyzedAt);
    }

    [Fact]
    public async Task TheDashboardStatus_NeedsTheMetricsPermission_AndShowsNoOtherCompany()
    {
        using var a = await StartChannelAsync(_factory, _client);
        using var b = await StartChannelAsync(_factory, _client);
        var seeded = await SeedConversationAsync(_factory.Services, b, DateTime.UtcNow.AddDays(-1), [In(0, "Hola")]);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlAsync(
                $"UPDATE \"Conversations\" SET \"LlmAnalyzedAt\" = now() WHERE \"Id\" = {seeded.ConversationId}");
        }

        Assert.Null((await a.Tenant.Owner.Client.GetFromJsonAsync<AiAnalysisStatusResponse>("/api/tenant/ai-analysis"))!.LastUpdatedAt);
        using var member = await a.Tenant.AddMemberAsync("noai", Domain.Enums.Permission.ConversationsView);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.GetAsync("/api/tenant/ai-analysis")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/tenant/ai-analysis")).StatusCode);
    }
}
