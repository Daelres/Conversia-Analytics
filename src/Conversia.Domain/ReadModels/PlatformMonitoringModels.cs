namespace Conversia.Domain.ReadModels;

/// <param name="Now">The time the snapshot is taken at (UTC).</param>
/// <param name="Month">The current month (America/Bogota), first day.</param>
/// <param name="Today">Today (America/Bogota).</param>
/// <param name="FirstDay">The first day of the daily series (America/Bogota).</param>
/// <param name="FirstDayStartUtc">The UTC instant <paramref name="FirstDay"/> starts at.</param>
/// <param name="TimeZoneId">The IANA id the days and months are counted in (PostgreSQL <c>AT TIME ZONE</c>).</param>
/// <param name="SilentLineHours">A company with active real lines and no real delivery for this long is "silent".</param>
/// <param name="DefaultAiQuota">The AI quota of a company without its own.</param>
/// <param name="AiUsdPerQuotaUnit">The company's USD ceiling per quota unit.</param>
/// <param name="ListLimit">How many companies each list of the overview returns at most.</param>
public sealed record MonitoringRequest(
    DateTime Now, DateOnly Month, DateOnly Today, DateOnly FirstDay, DateTime FirstDayStartUtc, string TimeZoneId, int SilentLineHours, int DefaultAiQuota, decimal AiUsdPerQuotaUnit, int ListLimit);

/// <summary>Webhook deliveries in a window: events stored, and the message counts the processor recorded on them. Never the payload.</summary>
public sealed record DeliveryCounts(int Events, int StoredMessages, int DuplicateMessages, int SkippedMessages, int FailedEvents)
{
    public static DeliveryCounts Zero { get; } = new(0, 0, 0, 0, 0);
}

public sealed record DeliveryWindows(DeliveryCounts Real24h, DeliveryCounts Sandbox24h, DeliveryCounts Real7d, DeliveryCounts Sandbox7d);

/// <param name="WebhookPending">Pending webhook events a worker will take (of Active companies).</param>
/// <param name="ClassificationStopped">Flagged conversations the worker gave up on after the maximum failures.</param>
/// <param name="BillingPastDue">Platform only (docs/BILLING.md): subscriptions past due, of which <paramref name="BillingSuspended"/> suspended for billing;
/// gateway events that could not be processed; attempts pending for more than a day.</param>
/// <param name="StuckDeletions">Platform only: data deletions of cancelled companies marked stuck, or started more than a day ago and not finished.</param>
public sealed record BacklogCounts(
    int WebhookPending, DateTime? OldestPendingAt, int ClassificationWaiting, DateTime? OldestClassificationAt, int ClassificationStopped, int StuckDeletions = 0,
    int BillingPastDue = 0, int BillingSuspended = 0, int BillingEventsFailed = 0, int BillingAttemptsStuck = 0,
    int BillingNeedsReview = 0, int DisconnectedChannels = 0, int MediaPending = 0, int MediaFailed24h = 0,
    int FileDeletionsQueued = 0, DateTime? OldestFileDeletionAt = null);

/// <summary>AI usage of one month, real or sandbox (summed over companies on the platform view).</summary>
public sealed record AiUsageTotals(
    DateOnly Month, bool IsSandbox, int Conversations, int Calls, int FailedCalls, long InputTokens, long OutputTokens, long CacheReadTokens,
    long CacheWriteTokens, decimal EstimatedUsd);

/// <summary>Errors of one fingerprint: how many and when the last one was. No message, no stack, no request data.</summary>
public sealed record ErrorGroupCount(string Fingerprint, string Source, string Kind, int Count, DateTime LastOccurredAt);

public sealed record SendOutcomeCount(string Outcome, bool IsSandbox, int Count);

/// <summary>One day (America/Bogota) of a company: messages in and out, conversations opened and closed. Counts only.</summary>
public sealed record DailyActivity(DateOnly Day, bool IsSandbox, int MessagesIn, int MessagesOut, int ConversationsOpened, int ConversationsClosed);

/// <summary>A company with active real lines and no real delivery in the window.</summary>
public sealed record SilentTenant(Guid TenantId, string Name, int ActiveLines, DateTime? LastDeliveryAt);

/// <summary>A company whose AI is on and that used up its quota or its USD ceiling this month.</summary>
public sealed record AiLimitedTenant(Guid TenantId, string Name, int Conversations, int Quota, decimal EstimatedUsd, decimal CeilingUsd);

public sealed record PlatformActivity(
    BacklogCounts Backlog,
    DeliveryWindows Deliveries,
    IReadOnlyList<AiUsageTotals> AiThisMonth,
    int Errors24h,
    IReadOnlyList<ErrorGroupCount> TopErrors24h,
    IReadOnlyList<SendOutcomeCount> Sends7d,
    int SilentTotal,
    IReadOnlyList<SilentTenant> Silent,
    int AiLimitedTotal,
    IReadOnlyList<AiLimitedTenant> AiLimited,
    LlmBatchCounts? Batches = null);

/// <summary>The platform's Message Batches by state.</summary>
public sealed record LlmBatchCounts(int Preparing, int Submitted, int Ended, int FailedLast24h, int CollectedLast24h, DateTime? OldestSubmittedAt);

/// <summary>One analysis window of a company: counts only.</summary>
public sealed record AiAnalysisRunCounts(
    DateTime WindowStart, DateTime WindowEnd, string Mode, DateTime StartedAt, DateTime LastActivityAt, int Claimed, int Analysed, int Unchanged, int Skipped,
    int Discarded, int Failed, int Deferred, int Batched);

public sealed record TenantActivity(
    BacklogCounts Backlog,
    DeliveryWindows Deliveries,
    DateTime? LastRealDeliveryAt,
    IReadOnlyList<AiUsageTotals> AiByMonth,
    int Errors24h,
    IReadOnlyList<ErrorGroupCount> TopErrors24h,
    IReadOnlyList<SendOutcomeCount> Sends7d,
    IReadOnlyList<DailyActivity> Days,
    int LlmPending = 0,
    AiAnalysisRunCounts? LastAnalysisRun = null);
