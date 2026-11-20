using Conversia.Application.Assistant;
using Conversia.Application.Classification.Llm;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Assistant;
using Conversia.Infrastructure.Classification.Usage;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The assistant's own usage category on a real PostgreSQL (docs/ASSISTANT.md, "Costs"): its per-company caps, the deployment cap it shares
/// with the analysis, and the record. Each test lives in a month of its own far in the past (1940 backwards: no other class uses them).
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class AssistantUsageMeterTests
{
    private static int _month;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AssistantUsageMeterTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static DateTime NewMonth() => new DateTime(1940, 6, 15, 17, 0, 0, DateTimeKind.Utc).AddMonths(-Interlocked.Increment(ref _month) * 2);

    private IServiceScopeFactory Scopes => _factory.Services.GetRequiredService<IServiceScopeFactory>();

    private AssistantUsageMeter Meter(TimeProvider clock, AiQuotaSettings? quota = null, AssistantLlmOptions? options = null)
        => new(Scopes, quota ?? AiQuotaSettings.Default, options ?? AssistantLlmOptions.Default with { TenantMonthlyUsdCap = 1m, SandboxMonthlyUsdPerCompany = 0.5m },
            clock, NullLogger<AssistantUsageMeter>.Instance);

    private async Task<Guid> CompanyAsync(bool aiEnabled = true)
    {
        using var tenant = await StartTenantAsync(_factory, _client, "assistant-usage");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Tenants.SingleAsync(t => t.Id == tenant.TenantId);
        if (aiEnabled)
        {
            stored.SetAiEnabled(true, "terms-test", DateTime.UtcNow);
        }

        await db.SaveChangesAsync();
        return tenant.TenantId;
    }

    private async Task<List<AssistantUsageMonth>> RowsAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AssistantUsageMonths.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == tenantId).OrderBy(u => u.IsSandbox).ToListAsync();
    }

    private static AssistantUsage Usage(decimal usd, int calls = 2) => new("claude-haiku-5-5", 900, 70, usd)
    {
        CacheReadTokens = 2500, CacheWriteTokens = 10, Calls = calls, GuardCalls = 1, FailedCalls = 0,
    };

    [Fact]
    public async Task AGeneration_IsRecorded_InItsOwnCategory_ApartFromTheAnalysis()
    {
        var tenant = await CompanyAsync();
        var meter = Meter(new FixedTimeProvider(NewMonth()));

        await meter.RecordUsageAsync(tenant, Guid.NewGuid(), isSandbox: false, Usage(0.0004m));
        await meter.RecordUsageAsync(tenant, Guid.NewGuid(), isSandbox: false, Usage(0.0002m, calls: 1));
        await meter.RecordUsageAsync(tenant, Guid.NewGuid(), isSandbox: true, Usage(0.0001m));

        var rows = await RowsAsync(tenant);
        Assert.Equal(2, rows.Count);
        var real = rows[0];
        Assert.Equal((false, 2, 3, 2, 1800L, 140L, 5000L, 20L, 0.0006m), (real.IsSandbox, real.Generations, real.Calls, real.GuardCalls, real.InputTokens, real.OutputTokens, real.CacheReadTokens, real.CacheWriteTokens, real.EstimatedUsd));
        Assert.True(rows[1].IsSandbox);
        using var scope = _factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AiUsageMonths.IgnoreQueryFilters().AnyAsync(u => u.TenantId == tenant));
    }

    [Fact]
    public async Task TheCompanysCap_AndItsSandboxBound_StopTheAssistant_Separately()
    {
        var tenant = await CompanyAsync();
        var meter = Meter(new FixedTimeProvider(NewMonth()));
        Assert.True(await meter.AdmitAsync(tenant, isSandbox: false));

        await meter.RecordUsageAsync(tenant, Guid.NewGuid(), isSandbox: true, Usage(0.5m));
        Assert.False(await meter.AdmitAsync(tenant, isSandbox: true));
        Assert.True(await meter.AdmitAsync(tenant, isSandbox: false));

        await meter.RecordUsageAsync(tenant, Guid.NewGuid(), isSandbox: false, Usage(1m));
        Assert.False(await meter.AdmitAsync(tenant, isSandbox: false));
        Assert.NotNull((await RowsAsync(tenant))[0].CapReachedAt);
    }

    [Fact]
    public async Task ACompanyWithTheAiOff_IsNeverAdmitted()
    {
        var tenant = await CompanyAsync(aiEnabled: false);
        Assert.False(await Meter(new FixedTimeProvider(NewMonth())).AdmitAsync(tenant, isSandbox: true));
    }

    [Fact]
    public async Task TheDeploymentCap_IsShared_WithTheAnalysis_BothWays()
    {
        var a = await CompanyAsync();
        var b = await CompanyAsync();
        var clock = new FixedTimeProvider(NewMonth());
        var quota = AiQuotaSettings.Default with { DeploymentMonthlyUsdCap = 1m };
        var assistant = Meter(clock, quota, AssistantLlmOptions.Default with { TenantMonthlyUsdCap = 100m });
        var analysis = new AiUsageMeter(Scopes, quota, clock, NullLogger<AiUsageMeter>.Instance);

        await analysis.RecordSuccessAsync(new AiUsageRecord(a, Guid.NewGuid(), false, new LlmUsage(1, 1, 0, 0), 0.6m));
        Assert.True(await assistant.AdmitAsync(b, isSandbox: false));
        await assistant.RecordUsageAsync(b, Guid.NewGuid(), isSandbox: false, Usage(0.4m));

        Assert.False(await assistant.AdmitAsync(b, isSandbox: false));
        Assert.Equal(AiAdmission.DeploymentCapReached, await analysis.CheckAdmissionAsync(a, isSandbox: false));
    }

    [Fact]
    public async Task ARecordThatCannotBeWritten_IsLogged_AndNeverThrows()
    {
        var meter = new AssistantUsageMeter(new BrokenScopes(), AiQuotaSettings.Default, AssistantLlmOptions.Default, new FixedTimeProvider(NewMonth()), NullLogger<AssistantUsageMeter>.Instance);

        await meter.RecordUsageAsync(Guid.NewGuid(), Guid.NewGuid(), isSandbox: false, Usage(0.001m));

        Assert.Equal(1, meter.LostRecords);
    }

    private sealed class BrokenScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("database unavailable");
    }
}
