using Conversia.Domain.Enums;

namespace Conversia.Application.Classification;

/// <summary>What a classifier concludes about how a sales conversation ended (so far). Maps to the opportunity stage.</summary>
public enum ClassificationOutcome
{
    /// <summary>Nothing in the conversation says where it stands (a greeting, media, only the business writing). The stage is left as it is.</summary>
    Undetermined = 0,

    /// <summary>The customer is interested or at least talking, and has neither booked nor refused.</summary>
    InFollowUp = 1,

    /// <summary>The customer asked for a booking and the business confirmed it (or the customer confirmed it after a proposal).</summary>
    Won = 2,

    /// <summary>The customer refused, or cancelled.</summary>
    Lost = 3,
}

/// <param name="Direction">Who wrote it, from the business point of view.</param>
/// <param name="Type">The provider's message type: <c>text</c>, <c>image</c>, <c>audio</c>, ...</param>
/// <param name="Text">The message text; null for media without caption. Customer data: it is read, never copied into a result.</param>
/// <param name="SentAt">UTC.</param>
public sealed record SnapshotMessage(MessageDirection Direction, string Type, string? Text, DateTime SentAt);

/// <summary>
/// Everything a classifier is given about one conversation: its messages in conversation order (oldest first) and the markers ingestion
/// derived from them. A snapshot is a value: classifying the same snapshot twice gives the same result.
/// </summary>
/// <param name="BranchName">The branch of the channel, as context (a classifier may know which services a branch offers).</param>
public sealed record ConversationSnapshot(
    Guid ConversationId,
    string? BranchName,
    DateTime StartedAt,
    DateTime? FirstInboundAt,
    DateTime? FirstResponseAt,
    IReadOnlyList<SnapshotMessage> Messages);

/// <summary>
/// What a classifier read. It holds codes and enums, never quoted customer text, so it can be stored, logged and compared freely.
/// </summary>
/// <param name="ServiceInterest">Normalized code(s) of the service or body zone asked about (<c>axilas</c>, <c>piernas,bikini</c>); null when none.</param>
/// <param name="InterestReason">Normalized code of why the customer is interested (<c>precio_promocion</c>); null when none.</param>
/// <param name="Objection">The latest objection the customer raised.</param>
/// <param name="LossReason">Only with <see cref="ClassificationOutcome.Lost"/>.</param>
/// <param name="Confidence">0 (a guess) to 1 (certain).</param>
/// <param name="RuleCodes">Short codes of the rules that fired (<c>outcome.won.advisor_confirmation</c>), in the order they were decided.</param>
public sealed record ClassificationResult(
    string? ServiceInterest,
    string? InterestReason,
    ObjectionType Objection,
    ClassificationOutcome Outcome,
    LossReason? LossReason,
    decimal Confidence,
    IReadOnlyList<string> RuleCodes);
