using Conversia.Application.Classification.Llm;
using Conversia.Application.Classification.Scheduling;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.Exceptions;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;

namespace Conversia.Application.Monitoring;

/// <summary>The health checks of the instance that answers, by name and status only.</summary>
public sealed record PlatformHealth(string Status, IReadOnlyList<(string Name, string Status)> Checks);

/// <summary>The health checks the API already runs for <c>/health</c> (webhook queue, database through it).</summary>
public interface IPlatformHealthProbe
{
    Task<PlatformHealth> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>The classification engine of the instance that answers: the provider and, with the LLM, its circuit breaker.</summary>
public interface IClassificationEngineStatus
{
    string Provider { get; }

    /// <summary>Null when the LLM is not in use.</summary>
    string? BreakerState(DateTime now);
}

/// <summary>The platform overview of this process for a short time (<c>PlatformMonitoring:OverviewCacheSeconds</c>): one instance, shared by requests.</summary>
public sealed class PlatformOverviewCache
{
    private readonly object _gate = new();
    private (PlatformOverviewResponse Value, DateTime Until)? _entry;

    public PlatformOverviewResponse? Get(DateTime now)
    {
        lock (_gate)
        {
            return _entry is { } entry && now < entry.Until ? entry.Value : null;
        }
    }

    public void Set(PlatformOverviewResponse value, DateTime until)
    {
        lock (_gate)
        {
            _entry = (value, until);
        }
    }
}

public interface IPlatformMonitoringService
{
    Task<PlatformOverviewResponse> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="TenantNotFoundException"/>.</summary>
    Task<TenantMonitoringResponse> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The platform administrator's monitoring (docs/PLATFORM_ADMIN.md, "Monitoring"): counts from <see cref="IPlatformMonitoringQueries"/>, the
/// health checks, the engine status and the threshold flags. Nothing private of a company: no text, no contact, no conversation.
/// </summary>
public sealed class PlatformMonitoringService : IPlatformMonitoringService
{
    /// <summary>How many companies each list of the overview shows.</summary>
    public const int ListLimit = 20;

    /// <summary>How many days the daily series of a company covers (today included).</summary>
    public const int SeriesDays = 14;

    private readonly IPlatformMonitoringQueries _queries;
    private readonly IPlatformAdminQueries _tenants;
    private readonly IPlatformHealthProbe _health;
    private readonly IClassificationEngineStatus _engine;
    private readonly MonitoringThresholds _thresholds;
    private readonly AiQuotaSettings _ai;
    private readonly TimeProvider _time;
    private readonly IAiUsageLossCounter? _lost;
    private readonly PlatformOverviewCache? _cache;
    private readonly AnalysisScheduleSettings _schedule;

    public PlatformMonitoringService(
        IPlatformMonitoringQueries queries, IPlatformAdminQueries tenants, IPlatformHealthProbe health, IClassificationEngineStatus engine,
        MonitoringThresholds thresholds, AiQuotaSettings ai, TimeProvider time, IAiUsageLossCounter? lost = null, PlatformOverviewCache? cache = null,
        AnalysisScheduleSettings? schedule = null)
    {
        _schedule = schedule ?? AnalysisScheduleSettings.Default;
        _cache = cache;
        _lost = lost;
        _queries = queries;
        _tenants = tenants;
        _health = health;
        _engine = engine;
        _thresholds = thresholds;
        _ai = ai;
        _time = time;
    }

    /// <summary>
    /// The overview, from this process's short cache when it is fresh (it is platform-wide: the same for every administrator, nothing of one
    /// company); a company's view is never cached.
    /// </summary>
    public async Task<PlatformOverviewResponse> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_cache?.Get(now) is { } cached)
        {
            return cached;
        }

        var fresh = await BuildOverviewAsync(now, cancellationToken);
        if (_thresholds.OverviewCacheSeconds > 0)
        {
            _cache?.Set(fresh, now.AddSeconds(_thresholds.OverviewCacheSeconds));
        }

        return fresh;
    }

    private async Task<PlatformOverviewResponse> BuildOverviewAsync(DateTime now, CancellationToken cancellationToken)
    {
        var request = Request(now);
        var health = await _health.CheckAsync(cancellationToken);
        var activity = await _queries.GetPlatformAsync(request, cancellationToken);
        var breaker = _engine.BreakerState(now);

        var real = Totals(activity.AiThisMonth, request.Month, sandbox: false);
        var sandbox = Totals(activity.AiThisMonth, request.Month, sandbox: true);
        var deploymentUsd = real.EstimatedUsd + sandbox.EstimatedUsd;
        var sends = Sends(activity.Sends7d);

        var flags = MonitoringFlags.Evaluate(
            new MonitoringFlagInput(
                health.Status,
                activity.Backlog.WebhookPending,
                Age(now, activity.Backlog.OldestPendingAt),
                activity.Backlog.ClassificationWaiting,
                Age(now, activity.Backlog.OldestClassificationAt),
                activity.Backlog.ClassificationStopped,
                activity.Deliveries.Real24h.Events,
                activity.Deliveries.Real24h.FailedEvents,
                activity.Errors24h,
                sends.RealLast7d,
                sends.RealFailedLast7d,
                activity.SilentTotal,
                activity.AiLimitedTotal,
                deploymentUsd >= _ai.DeploymentMonthlyUsdCap,
                breaker,
                _lost?.LostRecords ?? 0,
                activity.Backlog.StuckDeletions,
                activity.Backlog.BillingEventsFailed,
                activity.Backlog.BillingAttemptsStuck,
                activity.Backlog.BillingNeedsReview,
                activity.Batches?.FailedLast24h ?? 0),
            _thresholds);

        return new PlatformOverviewResponse
        {
            GeneratedAt = now,
            Health = new HealthResponse
            {
                Status = health.Status,
                Checks = health.Checks.Select(c => new HealthCheckResponse { Name = c.Name, Status = c.Status }).ToList(),
            },
            Backlog = Backlog(now, activity.Backlog),
            Deliveries = DeliveryWindowsResponse.From(activity.Deliveries),
            Ai = new PlatformAiResponse
            {
                Provider = _engine.Provider,
                BreakerState = breaker,
                Real = AiUsageTotalsResponse.From(real),
                Sandbox = AiUsageTotalsResponse.From(sandbox),
                DeploymentUsd = deploymentUsd,
                DeploymentCapUsd = _ai.DeploymentMonthlyUsdCap,
                LostUsageRecords = _lost?.LostRecords ?? 0,
                LastLostUsageAt = _lost?.LastLostAt,
                Batches = activity.Batches is { } b
                    ? new LlmBatchStatesResponse
                    {
                        Preparing = b.Preparing, Submitted = b.Submitted, Ended = b.Ended, FailedLast24h = b.FailedLast24h, CollectedLast24h = b.CollectedLast24h,
                        OldestSubmittedAt = b.OldestSubmittedAt,
                    }
                    : new LlmBatchStatesResponse(),
            },
            Errors = new ErrorsResponse { Last24h = activity.Errors24h, TopLast24h = activity.TopErrors24h.Select(ErrorGroupResponse.From).ToList() },
            Sends = sends,
            Flags = flags.Select(MonitoringFlagResponse.From).ToList(),
            SilentCompaniesTotal = activity.SilentTotal,
            SilentCompanies = activity.Silent
                .Select(s => new SilentCompanyResponse { TenantId = s.TenantId, Name = s.Name, ActiveLines = s.ActiveLines, LastDeliveryAt = s.LastDeliveryAt })
                .ToList(),
            AiLimitedCompaniesTotal = activity.AiLimitedTotal,
            AiLimitedCompanies = activity.AiLimited
                .Select(a => new AiLimitedCompanyResponse
                {
                    TenantId = a.TenantId, Name = a.Name, Conversations = a.Conversations, Quota = a.Quota, EstimatedUsd = a.EstimatedUsd, CeilingUsd = a.CeilingUsd,
                })
                .ToList(),
            Thresholds = new MonitoringThresholdsResponse
            {
                WebhookBacklog = _thresholds.WebhookBacklog,
                WebhookOldestPendingMinutes = _thresholds.WebhookOldestPendingMinutes,
                ClassificationBacklog = _thresholds.ClassificationBacklog,
                ClassificationOldestMinutes = _thresholds.ClassificationOldestMinutes,
                FailedDeliveryRatePercent = _thresholds.FailedDeliveryRatePercent,
                SilentLineHours = _thresholds.SilentLineHours,
                ErrorsLast24h = _thresholds.ErrorsLast24h,
                SendFailureRatePercent = _thresholds.SendFailureRatePercent,
            },
        };
    }

    public async Task<TenantMonitoringResponse> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var detail = await _tenants.GetTenantAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException();
        var summary = detail.Summary;
        var now = _time.GetUtcNow().UtcDateTime;
        var request = Request(now);
        var activity = await _queries.GetTenantAsync(tenantId, request, cancellationToken);

        var quota = _ai.EffectiveQuota(summary.AiMonthlyConversationQuota);
        var ceiling = _ai.CeilingUsd(quota);
        var real = Totals(activity.AiByMonth, request.Month, sandbox: false);
        var sandbox = Totals(activity.AiByMonth, request.Month, sandbox: true);

        return new TenantMonitoringResponse
        {
            TenantId = tenantId,
            GeneratedAt = now,
            SandboxMode = summary.SandboxMode,
            ActiveLines = summary.ActiveLines,
            MaxActiveLines = summary.MaxActiveLines,
            AssignedLicenses = detail.AssignedLicenses,
            LastActivityAt = summary.LastActivityAt,
            LastRealDeliveryAt = activity.LastRealDeliveryAt,
            Backlog = Backlog(now, activity.Backlog),
            Deliveries = DeliveryWindowsResponse.From(activity.Deliveries),
            Ai = new TenantAiUsageResponse
            {
                Enabled = summary.AiEnabled,
                State = AiUsageStates.Of(summary.AiEnabled, real.Conversations, quota, real.EstimatedUsd, ceiling).ToString(),
                Quota = quota,
                QuotaIsDefault = summary.AiMonthlyConversationQuota is null,
                CeilingUsd = ceiling,
                ThisMonth = AiUsageTotalsResponse.From(real),
                SandboxThisMonth = AiUsageTotalsResponse.From(sandbox),
                History = activity.AiByMonth.OrderByDescending(t => t.Month).ThenBy(t => t.IsSandbox).Select(AiUsageTotalsResponse.From).ToList(),
            },
            Errors = new ErrorsResponse { Last24h = activity.Errors24h, TopLast24h = activity.TopErrors24h.Select(ErrorGroupResponse.From).ToList() },
            Sends = Sends(activity.Sends7d),
            Days = activity.Days
                .OrderBy(d => d.Day).ThenBy(d => d.IsSandbox)
                .Select(d => new DailyActivityResponse
                {
                    Day = d.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    IsSandbox = d.IsSandbox,
                    MessagesIn = d.MessagesIn,
                    MessagesOut = d.MessagesOut,
                    ConversationsOpened = d.ConversationsOpened,
                    ConversationsClosed = d.ConversationsClosed,
                })
                .ToList(),
            AiAnalysis = new TenantAiAnalysisResponse
            {
                Mode = summary.AiAnalysisMode.ToString(),
                NightlyHour = summary.AiAnalysisMode == AiAnalysisMode.Nightly ? summary.AiAnalysisNightlyHour ?? _schedule.DefaultNightlyHour : null,
                IntervalHours = summary.AiAnalysisIntervalHours,
                NextWindowAt = AnalysisSchedule.NextWindow(
                    new TenantAnalysisSchedule(summary.AiAnalysisMode, summary.AiAnalysisNightlyHour, summary.AiAnalysisIntervalHours), tenantId, now, _schedule).OpensAt,
                Pending = activity.LlmPending,
                LastRun = activity.LastAnalysisRun is { } r
                    ? new AiAnalysisRunResponse
                    {
                        WindowStart = r.WindowStart, WindowEnd = r.WindowEnd, Mode = r.Mode, StartedAt = r.StartedAt, LastActivityAt = r.LastActivityAt, Claimed = r.Claimed,
                        Analysed = r.Analysed, Unchanged = r.Unchanged, Skipped = r.Skipped, Discarded = r.Discarded, Failed = r.Failed, Deferred = r.Deferred,
                        Batched = r.Batched,
                    }
                    : null,
            },
        };
    }

    private MonitoringRequest Request(DateTime now)
    {
        var today = _ai.DayOf(now);
        var firstDay = today.AddDays(-(SeriesDays - 1));
        var start = TimeZoneInfo.ConvertTimeToUtc(firstDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), _ai.TimeZone);
        return new(now, _ai.MonthOf(now), today, firstDay, start, _ai.TimeZone.Id, _thresholds.SilentLineHours, _ai.DefaultMonthlyConversations, _ai.UsdPerQuotaUnit, ListLimit);
    }

    private static AiUsageTotals Totals(IReadOnlyList<AiUsageTotals> rows, DateOnly month, bool sandbox)
        => rows.FirstOrDefault(r => r.Month == month && r.IsSandbox == sandbox) ?? new AiUsageTotals(month, sandbox, 0, 0, 0, 0, 0, 0, 0, 0m);

    private static SendsResponse Sends(IReadOnlyList<SendOutcomeCount> rows)
    {
        var real = rows.Where(r => !r.IsSandbox).ToList();
        return new SendsResponse
        {
            RealLast7d = real.Sum(r => r.Count),
            RealFailedLast7d = real.Where(r => r.Outcome != Domain.Entities.MessageSendOutcome.Sent).Sum(r => r.Count),
            ByOutcomeLast7d = rows
                .GroupBy(r => r.Outcome, StringComparer.Ordinal)
                .Select(g => new SendOutcomeResponse
                {
                    Outcome = g.Key,
                    Real = g.Where(r => !r.IsSandbox).Sum(r => r.Count),
                    Sandbox = g.Where(r => r.IsSandbox).Sum(r => r.Count),
                })
                .OrderByDescending(o => o.Real + o.Sandbox).ThenBy(o => o.Outcome, StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static BacklogResponse Backlog(DateTime now, BacklogCounts b) => new()
    {
        WebhookPending = b.WebhookPending,
        OldestPendingSeconds = Seconds(Age(now, b.OldestPendingAt)),
        ClassificationWaiting = b.ClassificationWaiting,
        OldestClassificationSeconds = Seconds(Age(now, b.OldestClassificationAt)),
        ClassificationStopped = b.ClassificationStopped,
        StuckDeletions = b.StuckDeletions,
        BillingPastDue = b.BillingPastDue,
        BillingSuspended = b.BillingSuspended,
        BillingEventsFailed = b.BillingEventsFailed,
        BillingAttemptsStuck = b.BillingAttemptsStuck,
        BillingNeedsReview = b.BillingNeedsReview,
    };

    private static TimeSpan? Age(DateTime now, DateTime? since) => since is { } at ? (now > at ? now - at : TimeSpan.Zero) : null;

    private static long? Seconds(TimeSpan? span) => span is { } s ? (long)s.TotalSeconds : null;
}
