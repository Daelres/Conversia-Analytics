using Conversia.Application.Monitoring;
using Conversia.Domain.ReadModels;

namespace Conversia.Application.DTOs.Admin;

/// <summary>One health check by name and status (<c>Healthy</c>, <c>Degraded</c>, <c>Unhealthy</c>). Nothing else of the check is exposed.</summary>
public class HealthCheckResponse
{
    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
}

public class HealthResponse
{
    public string Status { get; set; } = string.Empty;

    public IReadOnlyList<HealthCheckResponse> Checks { get; set; } = [];
}

/// <summary>Webhook deliveries of a window: events, stored, duplicate and skipped messages, failed events. Counts only.</summary>
public class DeliveryCountsResponse
{
    public int Events { get; set; }

    public int StoredMessages { get; set; }

    public int DuplicateMessages { get; set; }

    public int SkippedMessages { get; set; }

    public int FailedEvents { get; set; }

    public static DeliveryCountsResponse From(DeliveryCounts c) => new()
    {
        Events = c.Events, StoredMessages = c.StoredMessages, DuplicateMessages = c.DuplicateMessages, SkippedMessages = c.SkippedMessages, FailedEvents = c.FailedEvents,
    };
}

/// <summary>Real and sandbox deliveries, always apart.</summary>
public class DeliveryWindowsResponse
{
    public DeliveryCountsResponse RealLast24h { get; set; } = new();

    public DeliveryCountsResponse SandboxLast24h { get; set; } = new();

    public DeliveryCountsResponse RealLast7d { get; set; } = new();

    public DeliveryCountsResponse SandboxLast7d { get; set; } = new();

    public static DeliveryWindowsResponse From(DeliveryWindows w) => new()
    {
        RealLast24h = DeliveryCountsResponse.From(w.Real24h),
        SandboxLast24h = DeliveryCountsResponse.From(w.Sandbox24h),
        RealLast7d = DeliveryCountsResponse.From(w.Real7d),
        SandboxLast7d = DeliveryCountsResponse.From(w.Sandbox7d),
    };
}

public class BacklogResponse
{
    public int WebhookPending { get; set; }

    public long? OldestPendingSeconds { get; set; }

    public int ClassificationWaiting { get; set; }

    public long? OldestClassificationSeconds { get; set; }

    /// <summary>Conversations the worker gave up on after repeated failures (retried when a new message arrives).</summary>
    public int ClassificationStopped { get; set; }

    /// <summary>Platform only: data deletions of cancelled companies that do not finish (a count, no company named).</summary>
    public int StuckDeletions { get; set; }

    /// <summary>Platform only (docs/BILLING.md): subscriptions past due, of them suspended for billing; failed gateway events; attempts pending over a day.</summary>
    public int BillingPastDue { get; set; }

    public int BillingSuspended { get; set; }

    public int BillingEventsFailed { get; set; }

    public int BillingAttemptsStuck { get; set; }

    /// <summary>Attempts flagged for a person (money that may have moved without the charge showing it).</summary>
    public int BillingNeedsReview { get; set; }
}

/// <summary>AI usage of a month, real or sandbox. Sandbox usage never counts against a company's quota.</summary>
public class AiUsageTotalsResponse
{
    /// <summary><c>yyyy-MM</c>, America/Bogota.</summary>
    public string Month { get; set; } = string.Empty;

    public bool IsSandbox { get; set; }

    public int Conversations { get; set; }

    public int Calls { get; set; }

    public int FailedCalls { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long CacheReadTokens { get; set; }

    public long CacheWriteTokens { get; set; }

    public decimal EstimatedUsd { get; set; }

    public static AiUsageTotalsResponse From(AiUsageTotals t) => new()
    {
        Month = t.Month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
        IsSandbox = t.IsSandbox,
        Conversations = t.Conversations,
        Calls = t.Calls,
        FailedCalls = t.FailedCalls,
        InputTokens = t.InputTokens,
        OutputTokens = t.OutputTokens,
        CacheReadTokens = t.CacheReadTokens,
        CacheWriteTokens = t.CacheWriteTokens,
        EstimatedUsd = t.EstimatedUsd,
    };
}

/// <summary>The LLM side of the platform this month: real and sandbox usage, the deployment cap and the breaker of the instance that answered.</summary>
public class PlatformAiResponse
{
    /// <summary><c>Rules</c> or <c>Llm</c> (<c>Classification:Provider</c>).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary><c>Closed</c>, <c>Open</c> or <c>HalfOpen</c> on the API instance that answered; null when the LLM is not in use.</summary>
    public string? BreakerState { get; set; }

    public AiUsageTotalsResponse Real { get; set; } = new();

    public AiUsageTotalsResponse Sandbox { get; set; } = new();

    /// <summary>Real plus sandbox: what counts against the deployment cap.</summary>
    public decimal DeploymentUsd { get; set; }

    public decimal DeploymentCapUsd { get; set; }

    /// <summary>Usage records the answering instance could not write since it started (the cost is only in its log, event <c>AiUsageNotRecorded</c>).</summary>
    public long LostUsageRecords { get; set; }

    public DateTime? LastLostUsageAt { get; set; }
}

public class ErrorGroupResponse
{
    public string Fingerprint { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public int Count { get; set; }

    public DateTime LastOccurredAt { get; set; }

    public static ErrorGroupResponse From(ErrorGroupCount e) => new()
    {
        Fingerprint = e.Fingerprint, Source = e.Source, Kind = e.Kind, Count = e.Count, LastOccurredAt = e.LastOccurredAt,
    };
}

public class ErrorsResponse
{
    public int Last24h { get; set; }

    /// <summary>The most frequent fingerprints of the last 24 hours (counts only; the details are in the error registry, docs/ERRORS.md).</summary>
    public IReadOnlyList<ErrorGroupResponse> TopLast24h { get; set; } = [];
}

public class SendOutcomeResponse
{
    /// <summary><c>sent</c>, or the stable problem code of the refusal (docs/MESSAGING.md).</summary>
    public string Outcome { get; set; } = string.Empty;

    public int Real { get; set; }

    public int Sandbox { get; set; }
}

public class SendsResponse
{
    public int RealLast7d { get; set; }

    public int RealFailedLast7d { get; set; }

    public IReadOnlyList<SendOutcomeResponse> ByOutcomeLast7d { get; set; } = [];
}

public class MonitoringFlagResponse
{
    public string Code { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public static MonitoringFlagResponse From(MonitoringFlag f) => new() { Code = f.Code, Severity = f.Severity, Message = f.Message };
}

public class SilentCompanyResponse
{
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int ActiveLines { get; set; }

    public DateTime? LastDeliveryAt { get; set; }
}

public class AiLimitedCompanyResponse
{
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Conversations { get; set; }

    public int Quota { get; set; }

    public decimal EstimatedUsd { get; set; }

    public decimal CeilingUsd { get; set; }
}

/// <summary><c>GET /api/admin/monitoring</c>: the platform at a glance. Counts, statuses, codes and money only.</summary>
public class PlatformOverviewResponse
{
    public DateTime GeneratedAt { get; set; }

    public HealthResponse Health { get; set; } = new();

    public BacklogResponse Backlog { get; set; } = new();

    public DeliveryWindowsResponse Deliveries { get; set; } = new();

    public PlatformAiResponse Ai { get; set; } = new();

    public ErrorsResponse Errors { get; set; } = new();

    public SendsResponse Sends { get; set; } = new();

    public IReadOnlyList<MonitoringFlagResponse> Flags { get; set; } = [];

    public int SilentCompaniesTotal { get; set; }

    public IReadOnlyList<SilentCompanyResponse> SilentCompanies { get; set; } = [];

    public int AiLimitedCompaniesTotal { get; set; }

    public IReadOnlyList<AiLimitedCompanyResponse> AiLimitedCompanies { get; set; } = [];

    /// <summary>The thresholds the flags were computed with (<c>PlatformMonitoring:*</c>).</summary>
    public MonitoringThresholdsResponse Thresholds { get; set; } = new();
}

public class MonitoringThresholdsResponse
{
    public int WebhookBacklog { get; set; }

    public int WebhookOldestPendingMinutes { get; set; }

    public int ClassificationBacklog { get; set; }

    public int ClassificationOldestMinutes { get; set; }

    public int FailedDeliveryRatePercent { get; set; }

    public int SilentLineHours { get; set; }

    public int ErrorsLast24h { get; set; }

    public int SendFailureRatePercent { get; set; }
}

/// <summary>One day of a company (America/Bogota): real and sandbox apart.</summary>
public class DailyActivityResponse
{
    /// <summary><c>yyyy-MM-dd</c>.</summary>
    public string Day { get; set; } = string.Empty;

    public bool IsSandbox { get; set; }

    public int MessagesIn { get; set; }

    public int MessagesOut { get; set; }

    public int ConversationsOpened { get; set; }

    public int ConversationsClosed { get; set; }
}

/// <summary>The company's AI usage this month against its quota and ceiling (real usage only; sandbox shown apart).</summary>
public class TenantAiUsageResponse
{
    public bool Enabled { get; set; }

    /// <summary><c>Disabled</c>, <c>Ok</c>, <c>Warning</c> (80 % or more) or <c>Exhausted</c>.</summary>
    public string State { get; set; } = string.Empty;

    public int Quota { get; set; }

    public bool QuotaIsDefault { get; set; }

    public decimal CeilingUsd { get; set; }

    public AiUsageTotalsResponse ThisMonth { get; set; } = new();

    public AiUsageTotalsResponse SandboxThisMonth { get; set; } = new();

    /// <summary>The last three months (this one included), real and sandbox.</summary>
    public IReadOnlyList<AiUsageTotalsResponse> History { get; set; } = [];
}

/// <summary><c>GET /api/admin/tenants/{id}/monitoring</c>: the same counts scoped to one company. No conversation, message or contact.</summary>
public class TenantMonitoringResponse
{
    public Guid TenantId { get; set; }

    public DateTime GeneratedAt { get; set; }

    public bool SandboxMode { get; set; }

    public int ActiveLines { get; set; }

    public int MaxActiveLines { get; set; }

    public int AssignedLicenses { get; set; }

    /// <summary>The latest real message (sandbox excluded); null when none.</summary>
    public DateTime? LastActivityAt { get; set; }

    public DateTime? LastRealDeliveryAt { get; set; }

    public BacklogResponse Backlog { get; set; } = new();

    public DeliveryWindowsResponse Deliveries { get; set; } = new();

    public TenantAiUsageResponse Ai { get; set; } = new();

    public ErrorsResponse Errors { get; set; } = new();

    public SendsResponse Sends { get; set; } = new();

    public IReadOnlyList<DailyActivityResponse> Days { get; set; } = [];
}
