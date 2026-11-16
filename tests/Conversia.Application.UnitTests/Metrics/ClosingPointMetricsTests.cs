using Conversia.Application.DTOs.Metrics;
using Conversia.Application.Interfaces;
using Conversia.Application.Metrics;
using Conversia.Application.Services;
using Conversia.Application.Time;
using Conversia.Tests.Shared;

namespace Conversia.Application.UnitTests.Metrics;

/// <summary>"Hasta dónde llegan las conversaciones" (docs/METRICS.md, "Closing point"): the arithmetic over what the database counted.</summary>
public class ClosingPointMetricsTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Queries : IMetricsQueries
    {
        public IReadOnlyList<ClosingPointRaw> Rows { get; set; } = [];

        public DateTime? StaleBefore { get; private set; }

        public MetricsFilter? Filter { get; private set; }

        public Task<IReadOnlyList<ClosingPointRaw>> ClosingPointAsync(MetricsFilter filter, DateTime staleBeforeUtc, CancellationToken cancellationToken = default)
        {
            Filter = filter;
            StaleBefore = staleBeforeUtc;
            return Task.FromResult(Rows);
        }

        public Task<IReadOnlyList<ConversionRaw>> ConversionAsync(MetricsFilter filter, MetricsGroupBy by, DateTime matureBeforeUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResponseRaw>> FirstResponseAsync(MetricsFilter filter, MetricsGroupBy by, TimeSpan sla, DateTime nowUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LossReasonRaw>> LossReasonsAsync(MetricsFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ObjectionRaw>> ObjectionsAsync(MetricsFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DayRaw>> ConversationsPerDayAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DayMessagesRaw>> MessagesPerDayAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HourRaw>> ConversationsPerHourOfWeekAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HourRaw>> InboundMessagesPerHourOfWeekAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(int Total, IReadOnlyList<UnansweredRaw> Items)> UnansweredAsync(MetricsFilter filter, DateTime olderThanUtc, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private readonly Queries _queries = new();

    private Task<ClosingPointResponse> GetAsync(MetricsQuery? query = null)
        => new MetricsService(_queries, MetricsOptions.Default, new TenantTimeZone("America/Bogota"), new FixedTimeProvider(Now)).GetClosingPointAsync(query ?? new MetricsQuery());

    [Fact]
    public async Task TheStoppedConversations_AreSharedByStage_AndTheMainDropOffIsTheLargest()
    {
        _queries.Rows =
        [
            new("PriceShared", "None", Stopped: true, 6),
            new("PriceShared", "PhoneCall", Stopped: true, 2),
            new("ObjectionRaised", "None", Stopped: true, 4),
            new("FirstContact", "None", Stopped: true, 3),
            new(null, "None", Stopped: true, 5), // not read yet: counted apart
            new("AppointmentBooked", "None", Stopped: false, 9), // won or still alive: not stopped
            new("NeedsIdentified", "InPerson", Stopped: false, 1),
        ];

        var response = await GetAsync();

        Assert.Equal((30, 20, 5), (response.Conversations, response.Stopped, response.NotRead));
        Assert.Equal(
            [("FirstContact", 3, (double?)20.0), ("NeedsIdentified", 0, 0.0), ("PriceShared", 8, 53.3), ("ObjectionRaised", 4, 26.7), ("AppointmentProposed", 0, 0.0), ("AppointmentBooked", 0, 0.0), ("PaymentOrDeposit", 0, 0.0)],
            response.Stages.Select(s => (s.Stage, s.Count, s.SharePercent)));
        Assert.Equal("PriceShared", response.MainDropOff);
    }

    [Fact]
    public async Task TheMoveOffTheChat_IsAShareOfTheWholeCohort_ByKind()
    {
        _queries.Rows =
        [
            new("PriceShared", "PhoneCall", Stopped: true, 2),
            new("AppointmentBooked", "PhoneCall", Stopped: false, 1),
            new("NeedsIdentified", "InPerson", Stopped: false, 1),
            new("FirstContact", "None", Stopped: false, 6),
        ];

        var moved = (await GetAsync()).MovedOffChannel;

        Assert.Equal((4, (double?)40.0), (moved.Count, moved.SharePercent));
        Assert.Equal([("PhoneCall", 3, (double?)75.0), ("InPerson", 1, 25.0), ("OtherChannel", 0, 0.0)], moved.ByMove.Select(m => (m.Move, m.Count, m.SharePercent)));
    }

    [Fact]
    public async Task ATie_GoesToTheEarliestStage()
    {
        _queries.Rows = [new("ObjectionRaised", "None", true, 3), new("NeedsIdentified", "None", true, 3)];

        Assert.Equal("NeedsIdentified", (await GetAsync()).MainDropOff);
    }

    [Fact]
    public async Task NothingToShow_IsNullNotZero()
    {
        var response = await GetAsync();

        Assert.Equal((0, 0, (string?)null, (double?)null), (response.Conversations, response.Stopped, response.MainDropOff, response.MovedOffChannel.SharePercent));
        Assert.All(response.Stages, s => Assert.Null(s.SharePercent));
        Assert.Equal(7, response.Stages.Count);
    }

    [Fact]
    public async Task ItUsesTheFiltersOfTheQuery_AndTheMaturityAsTheIdleCutoff()
    {
        var branch = Guid.NewGuid();

        await GetAsync(new MetricsQuery { BranchId = branch, Source = "Ads" });

        Assert.Equal(branch, _queries.Filter!.BranchId);
        Assert.Equal(Domain.Enums.ConversationSource.Ads, _queries.Filter.Source);
        Assert.Equal(Now.AddDays(-MetricsOptions.Default.ConversionMaturityDays), _queries.StaleBefore);
    }
}
