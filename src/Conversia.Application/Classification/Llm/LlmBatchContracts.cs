namespace Conversia.Application.Classification.Llm;

/// <summary>One request of a Message Batch: our custom id (conversation id and a hash prefix, no personal data) and the same request a
/// synchronous call would send.</summary>
public sealed record LlmBatchItem(string CustomId, LlmRequest Request);

/// <param name="ProviderBatchId">The provider's id of the batch (<c>msgbatch_...</c>).</param>
/// <param name="ExpiresAt">When the provider stops processing it (24 h after creation).</param>
public sealed record LlmBatchSubmission(string ProviderBatchId, DateTime? ExpiresAt);

/// <summary>The provider's processing status of a batch.</summary>
public enum LlmBatchProcessing
{
    InProgress,
    Canceling,
    Ended,
}

/// <summary>A batch as the provider reports it: status and the counts of its requests.</summary>
public sealed record LlmBatchState(LlmBatchProcessing Processing, int InFlight, int Succeeded, int Errored, int Canceled, int Expired, DateTime? ExpiresAt);

/// <summary>The four result types of a batched request (only <see cref="Succeeded"/> is billed).</summary>
public enum LlmBatchResultKind
{
    Succeeded,
    Errored,
    Canceled,
    Expired,
}

/// <param name="Completion">With <see cref="LlmBatchResultKind.Succeeded"/>: the answer, read like a synchronous one.</param>
/// <param name="ErrorType">Otherwise: the provider's error type (a code, never text).</param>
public sealed record LlmBatchResult(string CustomId, LlmBatchResultKind Kind, LlmCompletion? Completion, string? ErrorType);

/// <summary>A batch as the provider lists it (for recovery: find a batch whose id was never saved).</summary>
/// <param name="Requests">All its requests, whatever their state (the sum of the provider's counts).</param>
public sealed record LlmBatchSummary(string ProviderBatchId, DateTime CreatedAt, LlmBatchProcessing Processing, int Requests);

/// <summary>
/// The Message Batches port (docs/CLASSIFICATION.md, "Batch mode"). The Anthropic adapter and the local replay client live in Infrastructure;
/// tests use fakes. Failures of the calls themselves are <see cref="LlmCallException"/>, never provider text.
/// </summary>
public interface ILlmBatchClient
{
    Task<LlmBatchSubmission> SubmitAsync(IReadOnlyList<LlmBatchItem> items, CancellationToken cancellationToken = default);

    Task<LlmBatchState> GetAsync(string providerBatchId, CancellationToken cancellationToken = default);

    /// <summary>Every result of an ended batch, in any order (match them by custom id).</summary>
    IAsyncEnumerable<LlmBatchResult> ResultsAsync(string providerBatchId, CancellationToken cancellationToken = default);

    /// <summary>The workspace's batches, newest first (the caller stops reading when they get older than it needs).</summary>
    IAsyncEnumerable<LlmBatchSummary> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the provider to stop a batch (requests not yet processed come back canceled, not billed).</summary>
    Task CancelAsync(string providerBatchId, CancellationToken cancellationToken = default);
}
