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
/// <param name="Id">The stored message id (part of the snapshot hash); null for snapshots built without the database.</param>
public sealed record SnapshotMessage(MessageDirection Direction, string Type, string? Text, DateTime SentAt, Guid? Id = null);

/// <summary>
/// Everything a classifier is given about one conversation: its messages in conversation order (oldest first) and the markers ingestion
/// derived from them. A snapshot is a value: classifying the same snapshot twice gives the same result.
/// </summary>
/// <param name="BranchName">The branch of the channel, as context (a classifier may know which services a branch offers).</param>
/// <param name="TenantId">The company; <see cref="Guid.Empty"/> for snapshots built without the database.</param>
/// <param name="KnownNames">
/// Names the system already knows for this conversation (the contact's display name, the assigned advisor, the branch). Customer data:
/// used only to mask them before any text leaves the process (<c>Pseudonymizer</c>) and, sorted, as an input of the snapshot hash.
/// </param>
/// <param name="KnownNumbers">Numbers the system already knows (the contact's WhatsApp id, the channel number); masked in any format before sending.</param>
/// <param name="ContactName">Which of <paramref name="KnownNames"/> is the customer (masked as <c>[CLIENTE]</c>).</param>
/// <param name="AdvisorName">Which of <paramref name="KnownNames"/> is the assigned advisor (masked as <c>[ASESOR]</c>).</param>
public sealed record ConversationSnapshot(
    Guid ConversationId,
    string? BranchName,
    DateTime StartedAt,
    DateTime? FirstInboundAt,
    DateTime? FirstResponseAt,
    IReadOnlyList<SnapshotMessage> Messages,
    Guid TenantId = default,
    IReadOnlyList<string>? KnownNames = null,
    IReadOnlyList<string>? KnownNumbers = null,
    string? ContactName = null,
    string? AdvisorName = null)
{
    /// <summary><see cref="KnownNames"/>, never null.</summary>
    public IReadOnlyList<string> Names => KnownNames ?? [];

    /// <summary><see cref="KnownNumbers"/>, never null.</summary>
    public IReadOnlyList<string> Numbers => KnownNumbers ?? [];
}

/// <summary>
/// What a classifier read. It holds codes and enums, never quoted customer text, so it can be stored, logged and compared freely.
/// </summary>
/// <param name="ServiceInterest">Normalized code(s) of the service or body zone asked about (<c>axilas</c>, <c>piernas,bikini</c>); null when none.</param>
/// <param name="InterestReason">Normalized code of why the customer is interested (<c>precio_promocion</c>); null when none.</param>
/// <param name="Objection">The latest objection the customer raised.</param>
/// <param name="LossReason">Only with <see cref="ClassificationOutcome.Lost"/>.</param>
/// <param name="Confidence">0 (a guess) to 1 (certain).</param>
/// <param name="RuleCodes">Short codes of the rules that fired (<c>outcome.won.advisor_confirmation</c>), in the order they were decided.</param>
/// <param name="AgeRange">The customer's stated age range (LLM only, and only when age extraction is enabled); null = not stated.</param>
/// <param name="Gender">The customer's stated gender (LLM only).</param>
/// <param name="WonEvidenceIndex">The message the LLM cites for a Won (1-based line of the prompt); informational only.</param>
/// <param name="IsRulesFallback">A rules result applied because the LLM failed or was not admitted (see <c>ClassificationUpdate.IsRulesFallback</c>).</param>
public sealed record ClassificationResult(
    string? ServiceInterest,
    string? InterestReason,
    ObjectionType Objection,
    ClassificationOutcome Outcome,
    LossReason? LossReason,
    decimal Confidence,
    IReadOnlyList<string> RuleCodes,
    AgeRange? AgeRange = null,
    Gender Gender = Gender.Undetermined,
    int? WonEvidenceIndex = null,
    bool IsRulesFallback = false);
