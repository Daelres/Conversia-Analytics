namespace Conversia.Application.Classification;

/// <summary>
/// The port behind which conversations are read: today a rule-based Spanish classifier (<c>Classification:Provider = Rules</c>), later an
/// LLM adapter that implements this same interface (see docs/CLASSIFICATION.md). An implementation must be deterministic for a given
/// snapshot as far as it can (the pipeline re-classifies a conversation every time it changes, and re-running must be harmless), and must
/// return codes and enums only, never customer text.
/// </summary>
public interface IConversationClassifier
{
    Task<ClassificationResult> ClassifyAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken = default);
}
