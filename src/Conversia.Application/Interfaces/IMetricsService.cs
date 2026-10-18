using Conversia.Application.DTOs.Metrics;
using Conversia.Domain.Enums;

namespace Conversia.Application.Interfaces;

/// <summary>The dashboard's numbers for the caller's tenant (docs/METRICS.md).</summary>
public interface IMetricsService
{
    Task<SummaryResponse> GetSummaryAsync(MetricsQuery query, CancellationToken cancellationToken = default);

    Task<ConversionResponse> GetConversionAsync(MetricsQuery query, CancellationToken cancellationToken = default);

    Task<FirstResponseResponse> GetFirstResponseAsync(MetricsQuery query, CancellationToken cancellationToken = default);

    Task<LossReasonsResponse> GetLossReasonsAsync(MetricsQuery query, CancellationToken cancellationToken = default);

    Task<VolumeResponse> GetVolumeAsync(MetricsQuery query, CancellationToken cancellationToken = default);

    Task<AlertsResponse> GetAlertsAsync(MetricsQuery query, CancellationToken cancellationToken = default);
}

/// <summary>The cohort of a metrics query, resolved: UTC instants (<c>FromUtc</c> inclusive, <c>ToUtc</c> exclusive) and the filters.</summary>
public sealed record MetricsFilter(DateTime FromUtc, DateTime ToUtc, Guid? BranchId, Guid? AdvisorId, bool Unassigned, ConversationSource? Source)
{
    public MetricsFilter WithoutBranch() => this with { BranchId = null };

    public MetricsFilter ForPeriod(DateTime fromUtc, DateTime toUtc) => this with { FromUtc = fromUtc, ToUtc = toUtc };
}

public enum MetricsGroupBy
{
    None = 0,
    Branch = 1,
    Advisor = 2,
    Source = 3,
}

/// <summary>One group of an aggregation. <c>GroupId</c> is the branch or advisor (null for the unassigned bucket and for sources); <c>GroupName</c> the name, or the source.</summary>
public sealed record ConversionRaw(Guid? GroupId, string? GroupName, int Total, int Won, int Lost, int InFollowUp, int New, int Stale);

public sealed record ResponseRaw(
    Guid? GroupId, string? GroupName, int Conversations, int Answered, int Unanswered,
    double? AvgSeconds, double? MedianSeconds, double? P90Seconds, int WithinSla, int UnansweredOverSla, DateTime? OldestUnansweredInboundAt);

public sealed record LossReasonRaw(string? Reason, int Count);

public sealed record ObjectionRaw(string Objection, int Count);

public sealed record DayRaw(DateOnly Date, int Count);

public sealed record DayMessagesRaw(DateOnly Date, int Inbound, int Outbound);

public sealed record HourRaw(int DayOfWeek, int Hour, int Count);

public sealed record UnansweredRaw(Guid ConversationId, Guid BranchId, string? BranchName, Guid? AdvisorId, string? AdvisorName, DateTime FirstInboundAt);

/// <summary>
/// The aggregations behind the metrics, computed by the database for the caller's tenant: the rows are groups, never conversations.
/// The tenant is taken from the current request; none of these methods loads a table into memory.
/// </summary>
public interface IMetricsQueries
{
    /// <summary>One row per group (a single row, even for an empty cohort, with <see cref="MetricsGroupBy.None"/>).</summary>
    Task<IReadOnlyList<ConversionRaw>> ConversionAsync(MetricsFilter filter, MetricsGroupBy by, DateTime matureBeforeUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResponseRaw>> FirstResponseAsync(MetricsFilter filter, MetricsGroupBy by, TimeSpan sla, DateTime nowUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LossReasonRaw>> LossReasonsAsync(MetricsFilter filter, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ObjectionRaw>> ObjectionsAsync(MetricsFilter filter, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DayRaw>> ConversationsPerDayAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DayMessagesRaw>> MessagesPerDayAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HourRaw>> ConversationsPerHourOfWeekAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HourRaw>> InboundMessagesPerHourOfWeekAsync(MetricsFilter filter, string timeZone, CancellationToken cancellationToken = default);

    /// <summary>The open conversations whose customer wrote before <paramref name="olderThanUtc"/> and nobody answered: the total and the oldest <paramref name="limit"/>.</summary>
    Task<(int Total, IReadOnlyList<UnansweredRaw> Items)> UnansweredAsync(MetricsFilter filter, DateTime olderThanUtc, int limit, CancellationToken cancellationToken = default);
}
