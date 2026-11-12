using System.Text.Json.Nodes;

namespace Conversia.Application.Classification.Llm;

/// <summary>
/// The port to a language model (design D3). One call, one answer: no retries (Stage A makes one attempt), no streaming. An implementation
/// returns the model's answer as it came (<see cref="LlmCompletion"/>) or throws <see cref="LlmCallException"/> naming the
/// <see cref="LlmFailureKind"/>; it never logs or rethrows text from the request or the answer. The Anthropic adapter and the local replay
/// client live in Infrastructure; tests use fakes.
/// </summary>
public interface ILlmClient
{
    Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}

/// <param name="SystemPrompt">The static instructions: first block of the cacheable prefix.</param>
/// <param name="ContextBlock">The catalog block: second block of the prefix, static per deployment; the cache breakpoint goes after it.</param>
/// <param name="UserContent">The pseudonymized conversation block.</param>
/// <param name="Schema">The JSON schema of the structured answer.</param>
/// <param name="SnapshotHash">The snapshot hash: never sent to a provider; the replay client keys its canned answers by it.</param>
public sealed record LlmRequest(string SystemPrompt, string ContextBlock, string UserContent, JsonObject Schema, string SnapshotHash);

/// <summary>Why the model stopped. Anything but <see cref="EndTurn"/> means the answer cannot be used as it is.</summary>
public enum LlmStopReason
{
    EndTurn,
    MaxTokens,
    Refusal,
    Other,
}

/// <summary>Tokens of one call as the provider billed them. Cache reads and writes are separate from <see cref="InputTokens"/>.</summary>
public sealed record LlmUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public static LlmUsage None { get; } = new(0, 0, 0, 0);
}

/// <param name="JsonText">The text of the answer (the structured JSON); null when the model wrote none.</param>
/// <param name="ModelId">The model that answered, as the provider names it.</param>
public sealed record LlmCompletion(LlmStopReason StopReason, string? JsonText, LlmUsage Usage, string ModelId);

/// <summary>
/// What went wrong with a call (design "Fallback and retry"). <b>Transport</b> kinds (the provider could not be reached or did not answer)
/// feed only the circuit breaker; <b>content</b> kinds (an answer that cannot be used) are counted per conversation in
/// <c>Conversation.LlmFailures</c>.
/// </summary>
public enum LlmFailureKind
{
    Timeout,
    RateLimited,
    Unavailable,
    Refused,
    Truncated,
    InvalidOutput,
    Misconfigured,
}

public static class LlmFailureKinds
{
    public static bool IsTransport(this LlmFailureKind kind) => kind is LlmFailureKind.Timeout or LlmFailureKind.RateLimited or LlmFailureKind.Unavailable;
}

/// <summary>
/// A failed call. <see cref="Exception.Message"/> holds only the kind and <see cref="ErrorType"/> (the type name of the provider's
/// exception), never request or answer text, so it is safe to log.
/// </summary>
public sealed class LlmCallException(LlmFailureKind kind, string errorType)
    : Exception($"LLM call failed: {kind} ({errorType}).")
{
    public LlmFailureKind Kind { get; } = kind;

    public string ErrorType { get; } = errorType;
}
