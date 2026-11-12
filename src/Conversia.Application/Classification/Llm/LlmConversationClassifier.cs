using System.Diagnostics;

namespace Conversia.Application.Classification.Llm;

/// <param name="ModelId">The model asked (<c>claude-haiku-5-5</c>); an input of the snapshot hash.</param>
/// <param name="ExtractAgeRange">Whether the age range is asked (design D17); an input of the snapshot hash.</param>
/// <param name="PreprocessingFingerprint">The preprocessor version and settings (<see cref="ConversationPreprocessorOptions.Fingerprint"/>).</param>
/// <param name="RulesFingerprint">The deployment's rules (<see cref="ClassificationRuleSet.Fingerprint"/>): they feed the Won guard and the catalog.</param>
/// <param name="HashKey">Optional per-deployment secret: the stored hash becomes HMAC-SHA256(key, snapshot hash). Null: the plain hash.</param>
public sealed record LlmClassifierSettings(string ModelId, bool ExtractAgeRange, string PreprocessingFingerprint, string RulesFingerprint = "", byte[]? HashKey = null)
{
    /// <summary>Never prints the key.</summary>
    public override string ToString() => $"LlmClassifierSettings {{ ModelId = {ModelId}, ExtractAgeRange = {ExtractAgeRange}, Keyed = {HashKey is { Length: > 0 }} }}";
}

/// <summary>Everything decided before a call: what would be sent, its hash, and whether there is anything of the customer's to read.</summary>
/// <param name="SnapshotHash">The snapshot hash (design D23) of the REAL snapshot; the digest is all that is ever stored.</param>
/// <param name="Prompt">The pseudonymized, compacted request.</param>
/// <param name="Stats">What the preprocessor did (sizes and counts only), for the log line of the call.</param>
/// <param name="HasCustomerText">False when, after preprocessing, no customer line is left (only advisor messages, or only media/empty
/// messages): calling the model then would only spend tokens, so the rules decide.</param>
public sealed record PreparedLlmCall(string SnapshotHash, LlmPrompt Prompt, PreprocessingStats Stats, bool HasCustomerText);

/// <summary>The outcome of one call: a mapped reading (before the Won guard) or a failure kind, and what it cost.</summary>
/// <param name="Error">A short code (mapper code or provider exception type), never text of the conversation or the answer.</param>
public sealed record LlmAttempt(ClassificationResult? Result, LlmFailureKind? Failure, string? Error, LlmUsage Usage, string ModelId, long LatencyMs);

/// <summary>
/// The LLM provider (design D2): real snapshot -> <see cref="Pseudonymizer"/> -> <see cref="ConversationPreprocessor"/> ->
/// <see cref="LlmPromptBuilder"/> -> <see cref="ILlmClient"/> -> <see cref="LlmResultMapper"/> (with the lines actually shown). It does not
/// implement <see cref="IConversationClassifier"/>: the coordinator needs the failure kind, the usage and the hash, which a
/// <see cref="ClassificationResult"/> cannot carry. A bad answer is never an exception: it is a content failure kind, and the rules decide.
/// </summary>
public sealed class LlmConversationClassifier
{
    private readonly ConversationPreprocessor _preprocessor;
    private readonly LlmPromptBuilder _promptBuilder;
    private readonly LlmResultMapper _mapper;
    private readonly ILlmClient _client;
    private readonly LlmClassifierSettings _settings;
    private readonly SnapshotHashInputs _hashInputs;

    public LlmConversationClassifier(
        ConversationPreprocessor preprocessor, LlmPromptBuilder promptBuilder, LlmResultMapper mapper, ILlmClient client, LlmClassifierSettings settings)
    {
        _preprocessor = preprocessor ?? throw new ArgumentNullException(nameof(preprocessor));
        _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hashInputs = new SnapshotHashInputs(
            promptBuilder.PromptVersion, settings.ModelId, ExtractionSchema.Version, settings.ExtractAgeRange, settings.PreprocessingFingerprint,
            settings.RulesFingerprint);
    }

    /// <param name="snapshot">The REAL snapshot, read with the known values (they decide what is masked).</param>
    public PreparedLlmCall Prepare(ConversationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var hash = SnapshotHasher.Compute(snapshot, _hashInputs);
        if (_settings.HashKey is { Length: > 0 } key)
        {
            // Keyed with the deployment's secret (Classification:Llm:HashKey): a stored digest cannot be confirmed by guessing a conversation.
            hash = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(hash)));
        }
        var conversation = _preprocessor.Process(Pseudonymizer.Pseudonymize(snapshot));
        var hasCustomerText = conversation.Entries.OfType<PromptLine>().Any(l => l.Role == PromptRole.Customer);
        return new PreparedLlmCall(hash, _promptBuilder.Build(conversation), conversation.Stats, hasCustomerText);
    }

    public async Task<LlmAttempt> CallAsync(PreparedLlmCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var request = new LlmRequest(call.Prompt.SystemPrompt, call.Prompt.ContextBlock, call.Prompt.UserContent, call.Prompt.Schema, call.SnapshotHash);
        var started = Stopwatch.GetTimestamp();

        LlmCompletion completion;
        try
        {
            completion = await _client.CompleteAsync(request, cancellationToken);
        }
        catch (LlmCallException ex)
        {
            return new LlmAttempt(null, ex.Kind, ex.ErrorType, LlmUsage.None, _settings.ModelId, Elapsed(started));
        }

        var latency = Elapsed(started);
        var failure = completion.StopReason switch
        {
            LlmStopReason.EndTurn => (LlmFailureKind?)null,
            LlmStopReason.Refusal => LlmFailureKind.Refused,
            LlmStopReason.MaxTokens => LlmFailureKind.Truncated,
            _ => LlmFailureKind.InvalidOutput,
        };
        if (failure is { } kind)
        {
            return new LlmAttempt(null, kind, "stop:" + completion.StopReason, completion.Usage, completion.ModelId, latency);
        }

        var mapping = _mapper.Map(completion.JsonText, call.Prompt.PresentLines);
        return mapping.Succeeded
            ? new LlmAttempt(mapping.Result, null, null, completion.Usage, completion.ModelId, latency)
            : new LlmAttempt(null, LlmFailureKind.InvalidOutput, mapping.Error, completion.Usage, completion.ModelId, latency);
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
