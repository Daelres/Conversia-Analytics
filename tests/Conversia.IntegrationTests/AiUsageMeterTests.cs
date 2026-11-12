using Conversia.Application.Classification.Llm;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Classification.Usage;
using Conversia.Infrastructure.Persistence;
using Conversia.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The metered AI admission and usage record on a real PostgreSQL (design D12, D13, D18; docs/CLASSIFICATION.md, "Usage and quota"). Each test
/// lives in a month of its own far in the past, so the deployment-wide sum of one test never sees another's.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class AiUsageMeterTests
{
    private static int _month;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AiUsageMeterTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // Mid-month at noon in Bogota, from 1960 backwards: no other test class uses these months.
    private static DateTime NewMonth() => new DateTime(1960, 6, 15, 17, 0, 0, DateTimeKind.Utc).AddMonths(-Interlocked.Increment(ref _month) * 2);

    private AiUsageMeter Meter(TimeProvider clock, AiQuotaSettings? settings = null)
        => new(_factory.Services.GetRequiredService<IServiceScopeFactory>(), settings ?? AiQuotaSettings.Default, clock, NullLogger<AiUsageMeter>.Instance);

    private async Task<Guid> CompanyAsync(bool aiEnabled = true, int? quota = null)
    {
        using var tenant = await StartTenantAsync(_factory, _client, "usage");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Tenants.SingleAsync(t => t.Id == tenant.TenantId);
        if (aiEnabled)
        {
            stored.SetAiEnabled(true, "terms-test", DateTime.UtcNow);
        }

        stored.SetAiMonthlyConversationQuota(quota);
        await db.SaveChangesAsync();
        return tenant.TenantId;
    }

    private async Task<List<AiUsageMonth>> RowsAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AiUsageMonths.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId == tenantId).OrderBy(u => u.Month).ThenBy(u => u.IsSandbox).ToListAsync();
    }

    private static AiUsageRecord Call(Guid tenant, Guid? conversation = null, bool sandbox = false, decimal usd = 0.001m, long input = 1000)
        => new(tenant, conversation ?? Guid.NewGuid(), sandbox, new LlmUsage(input, 50, 200, 10), usd);

    [Fact]
    public async Task AFreshCompany_HasTheAiOff_AndIsNeverAdmitted()
    {
        var tenant = await CompanyAsync(aiEnabled: false);

        Assert.Equal(AiAdmission.AiDisabled, await Meter(new FixedTimeProvider(NewMonth())).CheckAdmissionAsync(tenant, isSandbox: false));
        Assert.Equal(AiAdmission.AiDisabled, await Meter(new FixedTimeProvider(NewMonth())).CheckAdmissionAsync(tenant, isSandbox: true));
        Assert.Equal(AiAdmission.AiDisabled, await Meter(new FixedTimeProvider(NewMonth())).CheckAdmissionAsync(Guid.NewGuid(), isSandbox: false));
    }

    [Fact]
    public async Task ASuccess_CountsTheConversationOncePerMonth_AndAddsCallsTokensAndUsd()
    {
        var tenant = await CompanyAsync();
        var clock = new FixedTimeProvider(NewMonth());
        var meter = Meter(clock);
        var conversation = Guid.NewGuid();

        await meter.RecordSuccessAsync(Call(tenant, conversation, usd: 0.002m) with { WonDisagreement = true });
        await meter.RecordSuccessAsync(Call(tenant, conversation, usd: 0.003m));
        await meter.RecordFailureAsync(Call(tenant, Guid.NewGuid(), usd: 0.001m));

        var row = Assert.Single(await RowsAsync(tenant));
        Assert.Equal((AiQuotaSettings.Default.MonthOf(clock.GetUtcNow().UtcDateTime), false), (row.Month, row.IsSandbox));
        Assert.Equal((1, 3, 1, 1), (row.ConversationsAnalyzed, row.Calls, row.FailedCalls, row.WonDisagreements));
        Assert.Equal((3000L, 150L, 600L, 30L), (row.InputTokens, row.OutputTokens, row.CacheReadTokens, row.CacheWriteTokens));
        Assert.Equal(0.006m, row.EstimatedUsd);
    }

    [Fact]
    public async Task AFailedCall_ConsumesNoQuota()
    {
        var tenant = await CompanyAsync(quota: 1);
        var meter = Meter(new FixedTimeProvider(NewMonth()));

        await meter.RecordFailureAsync(Call(tenant));
        await meter.RecordFailureAsync(Call(tenant));

        Assert.Equal(AiAdmission.Allowed, await meter.CheckAdmissionAsync(tenant, isSandbox: false));
        Assert.Equal(0, Assert.Single(await RowsAsync(tenant)).ConversationsAnalyzed);
    }

    [Fact]
    public async Task TheQuota_IsExhaustedAtItsDistinctConversations_AndTheMomentIsKept()
    {
        var tenant = await CompanyAsync(quota: 2);
        var clock = new FixedTimeProvider(NewMonth());
        var meter = Meter(clock);

        await meter.RecordSuccessAsync(Call(tenant));
        Assert.Equal(AiAdmission.Allowed, await meter.CheckAdmissionAsync(tenant, isSandbox: false));
        await meter.RecordSuccessAsync(Call(tenant));

        Assert.Equal(AiAdmission.TenantExhausted, await meter.CheckAdmissionAsync(tenant, isSandbox: false));
        Assert.Equal(DatabaseTime.Truncate(clock.GetUtcNow().UtcDateTime), Assert.Single(await RowsAsync(tenant)).ExhaustedAt);
    }

    [Fact]
    public async Task TheUsdCeiling_StopsACompanyWithQuotaLeft()
    {
        var tenant = await CompanyAsync(quota: 10); // ceiling 0.20 USD
        var meter = Meter(new FixedTimeProvider(NewMonth()));

        await meter.RecordSuccessAsync(Call(tenant, usd: 0.20m));

        Assert.Equal(AiAdmission.TenantUsdCeiling, await meter.CheckAdmissionAsync(tenant, isSandbox: false));
        Assert.NotNull(Assert.Single(await RowsAsync(tenant)).UsdCeilingReachedAt);
    }

    [Fact]
    public async Task SandboxUsage_IsRecordedApart_AndNeverCountsAgainstTheBillableQuota()
    {
        var tenant = await CompanyAsync(quota: 1);
        var meter = Meter(new FixedTimeProvider(NewMonth()));

        await meter.RecordSuccessAsync(Call(tenant, sandbox: true));
        await meter.RecordSuccessAsync(Call(tenant, sandbox: true));

        Assert.Equal(AiAdmission.Allowed, await meter.CheckAdmissionAsync(tenant, isSandbox: false));
        Assert.Equal(AiAdmission.Allowed, await meter.CheckAdmissionAsync(tenant, isSandbox: true));
        var row = Assert.Single(await RowsAsync(tenant));
        Assert.Equal((true, 2, (DateTime?)null), (row.IsSandbox, row.ConversationsAnalyzed, row.ExhaustedAt));
    }

    [Fact]
    public async Task TheDeploymentCap_SumsEveryCompanysMonth_SandboxIncluded_AndStopsEveryone()
    {
        var a = await CompanyAsync();
        var b = await CompanyAsync();
        var c = await CompanyAsync();
        var settings = AiQuotaSettings.Default with { DeploymentMonthlyUsdCap = 1m };
        var meter = Meter(new FixedTimeProvider(NewMonth()), settings);

        await meter.RecordSuccessAsync(Call(a, sandbox: true, usd: 0.6m));
        Assert.Equal(AiAdmission.Allowed, await meter.CheckAdmissionAsync(c, isSandbox: false));
        await meter.RecordFailureAsync(Call(b, usd: 0.4m));

        Assert.Equal(AiAdmission.DeploymentCapReached, await meter.CheckAdmissionAsync(c, isSandbox: false));
        Assert.Equal(AiAdmission.DeploymentCapReached, await meter.CheckAdmissionAsync(a, isSandbox: true));
    }

    [Fact]
    public async Task ConcurrentSuccesses_NeverLoseAnIncrement_AndCountEachConversationOnce()
    {
        var tenant = await CompanyAsync(quota: 1000);
        var meter = Meter(new FixedTimeProvider(NewMonth()));
        var conversations = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();

        // 40 calls over 20 conversations, all at once: every call counts, every conversation once.
        await Task.WhenAll(conversations.Concat(conversations).Select(id => Task.Run(() => meter.RecordSuccessAsync(Call(tenant, id, usd: 0.001m)))));

        var row = Assert.Single(await RowsAsync(tenant));
        Assert.Equal((20, 40, 0.040m), (row.ConversationsAnalyzed, row.Calls, row.EstimatedUsd));
    }

    [Fact]
    public async Task TheMonth_IsTheBogotaMonthAtTheIncrement()
    {
        var tenant = await CompanyAsync();
        var first = NewMonth();
        var lastSecond = new DateTime(first.Year, first.Month, 1, 4, 59, 59, DateTimeKind.Utc).AddMonths(1); // 23:59:59 of the last day, Bogota
        var clock = new FixedTimeProvider(lastSecond);
        var meter = Meter(clock);
        var conversation = Guid.NewGuid();

        await meter.RecordSuccessAsync(Call(tenant, conversation));
        clock.Advance(TimeSpan.FromSeconds(3));
        await meter.RecordSuccessAsync(Call(tenant, conversation));

        var rows = await RowsAsync(tenant);
        Assert.Equal([new DateOnly(first.Year, first.Month, 1), new DateOnly(first.Year, first.Month, 1).AddMonths(1)], rows.Select(r => r.Month));
        Assert.All(rows, r => Assert.Equal(1, r.ConversationsAnalyzed)); // the same conversation counts again in a new month
    }

    [Fact]
    public async Task TheRetention_KeepsTheDistinctSetsOfTheCurrentAndThePreviousMonthOnly()
    {
        var tenant = await CompanyAsync();
        var now = NewMonth();
        var month = AiQuotaSettings.Default.MonthOf(now);
        foreach (var back in new[] { 3, 2, 1, 0 })
        {
            await Meter(new FixedTimeProvider(now.AddMonths(-back))).RecordSuccessAsync(Call(tenant));
        }

        // Older sets of other tests in the shared database go too: that is the rule.
        await Meter(new FixedTimeProvider(now)).PurgeConversationSetsAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var kept = await db.AiUsageConversations.IgnoreQueryFilters().Where(c => c.TenantId == tenant).Select(c => c.Month).OrderBy(m => m).ToListAsync();
        Assert.Equal([month.AddMonths(-1), month], kept);
        Assert.Equal(4, (await RowsAsync(tenant)).Count); // the month rows stay
    }

    [Fact]
    public async Task ACallerWhoseBudgetRanOut_DoesNotLoseTheUsage()
    {
        var tenant = await CompanyAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Meter(new FixedTimeProvider(NewMonth())).RecordSuccessAsync(Call(tenant), cancelled.Token);

        Assert.Equal(1, Assert.Single(await RowsAsync(tenant)).Calls);
    }

    private sealed class BrokenScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("database unreachable");
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, Microsoft.Extensions.Logging.EventId Event, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId, formatter(state, exception)));
    }

    [Fact]
    public async Task AUsageThatCannotBeWritten_IsLoggedForAlerting_Counted_AndNeverStopsTheClassification()
    {
        var logger = new CapturingLogger<AiUsageMeter>();
        var meter = new AiUsageMeter(new BrokenScopes(), AiQuotaSettings.Default, new FixedTimeProvider(NewMonth()), logger);

        await meter.RecordSuccessAsync(Call(Guid.NewGuid()));
        await meter.RecordFailureAsync(Call(Guid.NewGuid()));

        Assert.Equal(2, meter.LostRecords);
        Assert.NotNull(meter.LastLostAt);
        Assert.All(logger.Entries, e => Assert.Equal((Microsoft.Extensions.Logging.LogLevel.Error, AiUsageMeter.UsageNotRecorded), (e.Level, e.Event)));
        Assert.Equal(2, logger.Entries.Count);
        Assert.Equal("AiUsageNotRecorded", AiUsageMeter.UsageNotRecorded.Name);
    }

    [Fact]
    public async Task ACompanysSandbox_StopsAtItsOwnUsdBound_WithoutStoppingItsRealConversations()
    {
        var tenant = await CompanyAsync();
        var meter = Meter(new FixedTimeProvider(NewMonth()), AiQuotaSettings.Default with { SandboxMonthlyUsdPerCompany = 0.5m });

        await meter.RecordSuccessAsync(Call(tenant, sandbox: true, usd: 0.5m));

        Assert.Equal(AiAdmission.SandboxUsdCeiling, await meter.CheckAdmissionAsync(tenant, isSandbox: true));
        Assert.Equal(AiAdmission.Allowed, await meter.CheckAdmissionAsync(tenant, isSandbox: false));
    }
}
