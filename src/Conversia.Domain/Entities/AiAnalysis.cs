using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// What the LLM runner did for one company in one analysis window (docs/CLASSIFICATION.md, "When the AI reads"): counts only, never text or
/// conversation ids. Written by atomic upserts of the runner; read by the platform administrator's monitoring and, for the last time the AI
/// fields changed, by the company's dashboard.
/// </summary>
public class AiAnalysisRun
{
    public Guid TenantId { get; private set; }

    /// <summary>The window's start (UTC): its identity.</summary>
    public DateTime WindowStart { get; private set; }

    public DateTime WindowEnd { get; private set; }

    public AiAnalysisMode Mode { get; private set; }

    /// <summary>The first claim of the window.</summary>
    public DateTime StartedAt { get; private set; }

    public DateTime LastActivityAt { get; private set; }

    /// <summary>Conversations claimed (phase 1).</summary>
    public int Claimed { get; private set; }

    /// <summary>LLM readings applied.</summary>
    public int Analysed { get; private set; }

    /// <summary>Nothing the model reads had changed: no call.</summary>
    public int Unchanged { get; private set; }

    /// <summary>Not analysed: no customer text, not admitted, kept from the client.</summary>
    public int Skipped { get; private set; }

    /// <summary>Answers thrown away because the conversation changed during the call (left for the next window).</summary>
    public int Discarded { get; private set; }

    /// <summary>Unusable answers (refused, cut off, invalid).</summary>
    public int Failed { get; private set; }

    /// <summary>Calls that could not be made (transport failure, open breaker): retried later in the window.</summary>
    public int Deferred { get; private set; }

    /// <summary>Requests sent in a Message Batch.</summary>
    public int Batched { get; private set; }

    /// <summary>Calls made or requests batched: what the per-window cap counts (deferrals and unchanged conversations do not).</summary>
    public int Calls { get; private set; }

    // Required by EF Core for materialization; rows are written by SQL upserts.
    private AiAnalysisRun()
    {
    }
}

public enum LlmBatchStatus
{
    /// <summary>Rows written, not sent yet (a crash here leaves the batch abandoned: its conversations are released).</summary>
    Preparing,

    /// <summary>Sent to the provider; polled until it ends.</summary>
    Submitted,

    /// <summary>Ended at the provider; its results are being applied.</summary>
    Ended,

    /// <summary>Every result was applied (or found stale, failed, expired): done.</summary>
    Collected,

    /// <summary>Could not be sent, or abandoned before it was: its conversations go to the synchronous path.</summary>
    Failed,
}

public enum LlmBatchRequestStatus
{
    Pending,

    /// <summary>Taken by one collector (atomically, from Pending): its usage is recorded once, whatever happens next.</summary>
    Collecting,
    Applied,
    Discarded,
    Failed,
    Errored,
    Expired,
    Canceled,
}

/// <summary>
/// One Message Batch sent to the provider (docs/CLASSIFICATION.md, "Batch mode"). A platform table (a batch mixes companies), no text: the
/// provider's id, the state and the counts, so a restart resumes polling and collecting where it stopped.
/// </summary>
public class LlmBatch : BaseEntity
{
    public const int ProviderBatchIdMaxLength = 100;
    public const int ErrorMaxLength = 200;

    public string? ProviderBatchId { get; private set; }

    public LlmBatchStatus Status { get; private set; } = LlmBatchStatus.Preparing;

    public int RequestCount { get; private set; }

    /// <summary>When the batch started to be assembled (the runner's clock): a batch still preparing long after it was abandoned by a crash.</summary>
    public DateTime PreparedAt { get; private set; }

    /// <summary>When the upload to the provider started (the request count is final then). A batch with this set may exist at the provider.</summary>
    public DateTime? SubmitStartedAt { get; private set; }

    public DateTime? SubmittedAt { get; private set; }

    /// <summary>The provider's expiry (24 h after creation): requests not processed by then come back <c>expired</c>.</summary>
    public DateTime? ExpiresAt { get; private set; }

    public DateTime? LastPolledAt { get; private set; }

    public DateTime? EndedAt { get; private set; }

    public DateTime? CollectedAt { get; private set; }

    public int Succeeded { get; private set; }

    public int Errored { get; private set; }

    public int Expired { get; private set; }

    public int Canceled { get; private set; }

    /// <summary>The failure, as an exception type or a code: never provider text.</summary>
    public string? Error { get; private set; }

    private LlmBatch()
    {
    }

    public static LlmBatch Prepare(int requestCount, DateTime now)
    {
        if (requestCount < 1)
        {
            throw new DomainException("Un lote debe tener al menos una solicitud.");
        }

        return new LlmBatch { RequestCount = requestCount, PreparedAt = UtcTime.Normalize(now), CreatedAt = UtcTime.Normalize(now) };
    }

    /// <summary>The upload is about to start: from here on a provider batch may exist for these requests (recovery must look for it).</summary>
    public void MarkSubmitStarted(int requestCount, DateTime now)
    {
        if (requestCount < 1)
        {
            throw new DomainException("Un lote debe tener al menos una solicitud.");
        }

        RequestCount = requestCount;
        SubmitStartedAt = UtcTime.Normalize(now);
    }

    /// <summary>The provider accepted the batch. Only a batch still <see cref="LlmBatchStatus.Preparing"/> becomes submitted; returns false otherwise.</summary>
    public bool MarkSubmitted(string providerBatchId, DateTime? expiresAt, DateTime now)
    {
        if (Status != LlmBatchStatus.Preparing)
        {
            return false;
        }

        ProviderBatchId = TextRules.Required(providerBatchId, ProviderBatchIdMaxLength, "El identificador del lote no es válido.");
        Status = LlmBatchStatus.Submitted;
        SubmittedAt = UtcTime.Normalize(now);
        ExpiresAt = expiresAt is null ? null : UtcTime.Normalize(expiresAt.Value);
        return true;
    }

    public void RecordPoll(DateTime now) => LastPolledAt = UtcTime.Normalize(now);

    public void MarkEnded(int succeeded, int errored, int expired, int canceled, DateTime now)
    {
        Status = LlmBatchStatus.Ended;
        EndedAt ??= UtcTime.Normalize(now);
        (Succeeded, Errored, Expired, Canceled) = (succeeded, errored, expired, canceled);
    }

    public void MarkCollected(DateTime now)
    {
        Status = LlmBatchStatus.Collected;
        CollectedAt = UtcTime.Normalize(now);
    }

    public void MarkFailed(string error, DateTime now)
    {
        Status = LlmBatchStatus.Failed;
        Error = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        CollectedAt = UtcTime.Normalize(now);
    }
}

/// <summary>
/// One conversation in a <see cref="LlmBatch"/>: what phase 1 saw (hash, request time, stage) so phase 3 applies the answer only if nothing
/// changed. The custom id sent to the provider is the conversation id and a prefix of the hash: no personal data.
/// </summary>
public class LlmBatchRequest : BaseEntity
{
    public const int CustomIdMaxLength = 64;

    public Guid BatchId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    /// <summary>The analysis window the request was claimed in (its counts are added to that window's <see cref="AiAnalysisRun"/>).</summary>
    public DateTime WindowStart { get; private set; }

    public string CustomId { get; private set; } = string.Empty;

    public string SnapshotHash { get; private set; } = string.Empty;

    public bool IsSandbox { get; private set; }

    /// <summary><see cref="Conversation.ClassificationRequestedAt"/> at phase 1.</summary>
    public DateTime? RequestedAt { get; private set; }

    public OpportunityStage StageAtPlan { get; private set; }

    public DateTime? StageChangedAtPlan { get; private set; }

    public LlmBatchRequestStatus Status { get; private set; } = LlmBatchRequestStatus.Pending;

    /// <summary>The lease phase 1 gave the conversation: phase 3 applies only while the conversation still holds it.</summary>
    public DateTime? LeaseUntil { get; private set; }

    /// <summary>The estimated cost of the request at the batch price (prompt size, the answer ceiling): reserved in admission until it is resolved.</summary>
    public decimal EstimatedUsd { get; private set; }

    /// <summary>When a collector took it (<see cref="LlmBatchRequestStatus.Collecting"/>).</summary>
    public DateTime? CollectingAt { get; private set; }

    public DateTime? ResolvedAt { get; private set; }

    private LlmBatchRequest()
    {
    }

    /// <summary>The custom id of a request: <c>c</c>, the conversation id (32 hex) and the first 30 hex characters of the hash (64 in all).</summary>
    public static string CustomIdFor(Guid conversationId, string snapshotHash) => $"c{conversationId:N}-{snapshotHash[..30]}";

    public static LlmBatchRequest For(
        Guid batchId, Guid tenantId, Guid conversationId, DateTime windowStart, string snapshotHash, bool isSandbox, DateTime? requestedAt, OpportunityStage stage,
        DateTime? stageChangedAt, DateTime now, DateTime? leaseUntil = null, decimal estimatedUsd = 0m)
    {
        if (snapshotHash is null || snapshotHash.Length != Conversation.SnapshotHashLength)
        {
            throw new DomainException("La huella de la conversación no es válida.");
        }

        return new LlmBatchRequest
        {
            BatchId = batchId,
            TenantId = tenantId,
            ConversationId = conversationId,
            WindowStart = UtcTime.Normalize(windowStart),
            CustomId = CustomIdFor(conversationId, snapshotHash),
            SnapshotHash = snapshotHash,
            IsSandbox = isSandbox,
            RequestedAt = requestedAt,
            StageAtPlan = stage,
            StageChangedAtPlan = stageChangedAt,
            LeaseUntil = leaseUntil is null ? null : UtcTime.Normalize(leaseUntil.Value),
            EstimatedUsd = estimatedUsd,
            CreatedAt = UtcTime.Normalize(now),
        };
    }

    public void Resolve(LlmBatchRequestStatus status, DateTime now)
    {
        Status = status;
        ResolvedAt = UtcTime.Normalize(now);
    }
}
