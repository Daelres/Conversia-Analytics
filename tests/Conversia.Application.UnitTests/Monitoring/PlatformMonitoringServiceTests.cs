using Conversia.Application.Classification.Llm;
using Conversia.Application.Exceptions;
using Conversia.Application.Monitoring;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;
using Conversia.Tests.Shared;
using Moq;

namespace Conversia.Application.UnitTests.Monitoring;

/// <summary>How the monitoring service turns the counts into the overview and a company's view; the queries themselves are integration-tested.</summary>
public class PlatformMonitoringServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Month = new(2026, 11, 1);

    private readonly Mock<IPlatformMonitoringQueries> _queries = new();
    private readonly Mock<IPlatformAdminQueries> _tenants = new();
    private readonly Mock<IPlatformHealthProbe> _health = new();
    private readonly Mock<IClassificationEngineStatus> _engine = new();
    private MonitoringRequest? _asked;

    public PlatformMonitoringServiceTests()
    {
        _health.Setup(h => h.CheckAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PlatformHealth("Healthy", [("webhook-queue", "Healthy")]));
        _engine.SetupGet(e => e.Provider).Returns("Llm");
        _engine.Setup(e => e.BreakerState(It.IsAny<DateTime>())).Returns("Closed");
    }

    private readonly Mock<IAiUsageLossCounter> _lost = new();

    private readonly FixedTimeProvider _clock = new(Now);
    private readonly PlatformOverviewCache _cache = new();

    private PlatformMonitoringService CreateSut()
        => new(_queries.Object, _tenants.Object, _health.Object, _engine.Object, MonitoringThresholds.Default, AiQuotaSettings.Default, _clock,
            _lost.Object, _cache);

    private static readonly DeliveryWindows NoDeliveries = new(DeliveryCounts.Zero, DeliveryCounts.Zero, DeliveryCounts.Zero, DeliveryCounts.Zero);

    private static AiUsageTotals Usage(bool sandbox, int conversations, decimal usd) => new(Month, sandbox, conversations, conversations, 0, 100, 10, 0, 0, usd);

    [Fact]
    public async Task TheOverview_AsksForTheBogotaMonthAndFourteenDays_AndFlagsTheDeploymentCapWithSandboxIncluded()
    {
        _queries.Setup(q => q.GetPlatformAsync(It.IsAny<MonitoringRequest>(), It.IsAny<CancellationToken>()))
            .Callback<MonitoringRequest, CancellationToken>((r, _) => _asked = r)
            .ReturnsAsync(new PlatformActivity(
                new BacklogCounts(3, Now.AddMinutes(-2), 0, null, 0), NoDeliveries, [Usage(false, 10, 15m), Usage(true, 4, 5m)], 0, [], [], 0, [], 0, []));

        var overview = await CreateSut().GetOverviewAsync();

        Assert.Equal((Month, new DateOnly(2026, 10, 29), "America/Bogota", 24, 500), (_asked!.Month, _asked.FirstDay, _asked.TimeZoneId, _asked.SilentLineHours, _asked.DefaultAiQuota));
        Assert.Equal((20m, 20m), (overview.Ai.DeploymentUsd, overview.Ai.DeploymentCapUsd));
        Assert.Equal((10, 4), (overview.Ai.Real.Conversations, overview.Ai.Sandbox.Conversations));
        Assert.Contains(overview.Flags, f => f.Code == "ai_deployment_cap");
        Assert.Equal((3, 120L), (overview.Backlog.WebhookPending, overview.Backlog.OldestPendingSeconds!.Value));
        Assert.Equal(("Llm", "Closed"), (overview.Ai.Provider, overview.Ai.BreakerState));
        Assert.Equal(0, overview.Ai.LostUsageRecords);
    }

    [Fact]
    public async Task TheOverview_ShowsTheUsageRecordsThisInstanceCouldNotWrite()
    {
        _lost.SetupGet(l => l.LostRecords).Returns(3);
        _lost.SetupGet(l => l.LastLostAt).Returns(Now.AddMinutes(-1));
        _queries.Setup(q => q.GetPlatformAsync(It.IsAny<MonitoringRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformActivity(new BacklogCounts(0, null, 0, null, 0), NoDeliveries, [], 0, [], [], 0, [], 0, []));

        var overview = await CreateSut().GetOverviewAsync();

        Assert.Equal((3L, (DateTime?)Now.AddMinutes(-1)), (overview.Ai.LostUsageRecords, overview.Ai.LastLostUsageAt));
        Assert.Contains(overview.Flags, f => f.Code == "ai_usage_lost");
    }

    [Fact]
    public async Task ACompanysAiState_CountsItsRealUsageOnly_AgainstItsEffectiveQuota()
    {
        var tenant = Guid.NewGuid();
        _tenants.Setup(t => t.GetTenantAsync(tenant, It.IsAny<CancellationToken>())).ReturnsAsync(new TenantAdminDetail(
            new TenantAdminSummary(tenant, "Aurora", TenantStatus.Active, 4, 2, 2, null, Now, null, null, null, SandboxMode: true, AiEnabled: true,
                AiMonthlyConversationQuota: 100), [], AssignedLicenses: 2));
        _queries.Setup(q => q.GetTenantAsync(tenant, It.IsAny<MonitoringRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new TenantActivity(
            new BacklogCounts(0, null, 0, null, 0), NoDeliveries, null, [Usage(false, 80, 0.5m), Usage(true, 500, 9m)], 0, [], [], []));

        var view = await CreateSut().GetTenantAsync(tenant);

        Assert.Equal(("Warning", 100, false, 2m), (view.Ai.State, view.Ai.Quota, view.Ai.QuotaIsDefault, view.Ai.CeilingUsd));
        Assert.Equal((80, 500), (view.Ai.ThisMonth.Conversations, view.Ai.SandboxThisMonth.Conversations));
        Assert.Equal((true, 2, 2, 4), (view.SandboxMode, view.AssignedLicenses, view.ActiveLines, view.MaxActiveLines));
    }

    [Fact]
    public async Task AnUnknownCompany_IsNotFound()
        => await Assert.ThrowsAsync<TenantNotFoundException>(() => CreateSut().GetTenantAsync(Guid.NewGuid()));

    [Fact]
    public async Task TheOverview_IsCachedForAShortTime_AcrossRequests()
    {
        _queries.Setup(q => q.GetPlatformAsync(It.IsAny<MonitoringRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformActivity(new BacklogCounts(0, null, 0, null, 0), NoDeliveries, [], 0, [], [], 0, [], 0, []));

        var first = await CreateSut().GetOverviewAsync();
        _clock.Advance(TimeSpan.FromSeconds(MonitoringThresholds.Default.OverviewCacheSeconds - 1));
        var cached = await CreateSut().GetOverviewAsync(); // another request, another scoped service: the same cache
        _clock.Advance(TimeSpan.FromSeconds(2));
        var fresh = await CreateSut().GetOverviewAsync();

        _queries.Verify(q => q.GetPlatformAsync(It.IsAny<MonitoringRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Same(first, cached);
        Assert.NotSame(first, fresh);
    }

    [Fact]
    public async Task ACompanysView_IsNeverServedFromTheCache_NorAnotherCompanys()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        foreach (var id in new[] { a, b })
        {
            _tenants.Setup(t => t.GetTenantAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(new TenantAdminDetail(
                new TenantAdminSummary(id, id.ToString(), TenantStatus.Active, 4, id == a ? 1 : 3, 3, null, Now, null, null, null), []));
            _queries.Setup(q => q.GetTenantAsync(id, It.IsAny<MonitoringRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new TenantActivity(
                new BacklogCounts(id == a ? 1 : 2, null, 0, null, 0), NoDeliveries, null, [], 0, [], [], []));
        }

        _queries.Setup(q => q.GetPlatformAsync(It.IsAny<MonitoringRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformActivity(new BacklogCounts(9, null, 0, null, 0), NoDeliveries, [], 0, [], [], 0, [], 0, []));
        await CreateSut().GetOverviewAsync();

        var viewA = await CreateSut().GetTenantAsync(a);
        var viewB = await CreateSut().GetTenantAsync(b);
        var viewAAgain = await CreateSut().GetTenantAsync(a);

        Assert.Equal((a, 1, 1), (viewA.TenantId, viewA.ActiveLines, viewA.Backlog.WebhookPending));
        Assert.Equal((b, 3, 2), (viewB.TenantId, viewB.ActiveLines, viewB.Backlog.WebhookPending));
        Assert.NotSame(viewA, viewAAgain);
        _queries.Verify(q => q.GetTenantAsync(a, It.IsAny<MonitoringRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
