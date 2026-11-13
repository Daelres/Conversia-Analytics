namespace Conversia.Domain.Enums;

/// <summary>
/// When the LLM reads a company's conversations (docs/CLASSIFICATION.md, "When the AI reads"). The rules classifier is not affected: it reads every
/// conversation shortly after its last message, whatever the mode.
/// </summary>
public enum AiAnalysisMode
{
    /// <summary>Once a day, in a night window (the default: spreads the load and the cost).</summary>
    Nightly,

    /// <summary>Once every N hours (1 to 24), in windows counted from local midnight.</summary>
    EveryHours,

    /// <summary>A few minutes after the last message, like the first LLM stage: for special cases only.</summary>
    NearRealTime,
}
