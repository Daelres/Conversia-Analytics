using Conversia.Application.DTOs.Organization;
using Conversia.Application.Interfaces;
using Conversia.Application.Metrics;
using Conversia.Application.Services;
using Conversia.Application.Time;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>The metrics service of one tenant, over the real database, with a clock the test sets.</summary>
internal sealed class MetricsHarness : IDisposable
{
    private readonly IServiceScope _scope;

    public MetricsHarness(IServiceProvider services, Guid tenantId, DateTime now, string timeZone = "America/Bogota", MetricsOptions? options = null)
    {
        _scope = services.CreateScope();
        _scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        Service = new MetricsService(_scope.ServiceProvider.GetRequiredService<IMetricsQueries>(), options ?? MetricsOptions.Default, new TenantTimeZone(timeZone), new FixedTimeProvider(now));
    }

    public MetricsService Service { get; }

    public void Dispose() => _scope.Dispose();
}

/// <summary>A tenant with two branches, an advisor in each and a WhatsApp channel in each: the structure the metrics datasets hang on.</summary>
internal sealed class MetricsTenant : IDisposable
{
    private readonly ChannelFixture _fixture;

    private MetricsTenant(ChannelFixture fixture, BranchResponse second, CreateChannelResponse secondChannel, AdvisorResponse ana, AdvisorResponse beto)
    {
        _fixture = fixture;
        Second = second;
        SecondChannel = secondChannel;
        Ana = ana;
        Beto = beto;
    }

    public Guid TenantId => _fixture.TenantId;

    public HttpClient Owner => _fixture.Tenant.Owner.Client;

    public ChannelFixture Fixture => _fixture;

    /// <summary>"Centro" (the fixture's branch, named by the test) and "Norte".</summary>
    public BranchResponse Centro => _fixture.Branch;

    public BranchResponse Second { get; }

    public CreateChannelResponse SecondChannel { get; }

    public AdvisorResponse Ana { get; }

    public AdvisorResponse Beto { get; }

    public static async Task<MetricsTenant> StartAsync(CustomWebApplicationFactory factory, HttpClient anonymous, string centroName = "Centro", string northName = "Norte")
    {
        var tenant = await StartTenantAsync(factory, anonymous, "metrics");
        var centro = await CreateBranchAsync(tenant.Owner.Client, centroName);
        var north = await CreateBranchAsync(tenant.Owner.Client, northName);
        var channel = await CreateChannelAsync(tenant.Owner.Client, centro.Id, "+57 300 555 0100", "pnid-metrics-1");
        var northChannel = await CreateChannelAsync(tenant.Owner.Client, north.Id, "+57 300 555 0200", "pnid-metrics-2");
        var ana = await CreateAdvisorAsync(tenant.Owner.Client, centro.Id, "Ana");
        var beto = await CreateAdvisorAsync(tenant.Owner.Client, north.Id, "Beto");
        return new MetricsTenant(new ChannelFixture(tenant, centro, channel), north, northChannel, ana, beto);
    }

    public Task<Seeded> SeedAsync(
        IServiceProvider services, bool north, DateTime start, IReadOnlyList<Msg> messages, ConversationSource source = ConversationSource.Organic,
        Guid? advisorId = null, OpportunityStage stage = OpportunityStage.New, LossReason? loss = null, ObjectionType objection = ObjectionType.None, bool closed = false)
        => SeedConversationAsync(
            services,
            TenantId,
            north ? Second.Id : Centro.Id,
            north ? SecondChannel.Id : _fixture.Channel.Id,
            start,
            messages,
            source,
            advisorId,
            closed,
            configureOpportunity: o => Decide(o, start, stage, loss, objection));

    internal static void Decide(Opportunity opportunity, DateTime at, OpportunityStage stage, LossReason? loss, ObjectionType objection)
    {
        if (stage != OpportunityStage.New)
        {
            opportunity.SetManualOutcome(stage, loss, null, at);
        }

        if (objection != ObjectionType.None)
        {
            opportunity.ApplyClassification(new ClassificationUpdate(null, null, objection, null, null, 0.5m, null), at);
        }
    }

    public void Dispose() => _fixture.Dispose();
}
