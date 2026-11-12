using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// The exchange between one contact and one channel. While it is open, the contact's new messages on that channel belong to it.
/// The time markers are always derived from the messages (<see cref="ApplyTimeline"/>).
/// </summary>
public class Conversation : BaseEntity
{
    public const int SourceReferenceMaxLength = 500;

    public static readonly string SourceReferenceRuleMessage =
        $"La referencia del origen {TextRules.RuleText(SourceReferenceMaxLength, required: false)}.";

    public Guid TenantId { get; private set; }

    public Guid ContactId { get; private set; }

    public Guid ChannelId { get; private set; }

    /// <summary>The branch of the channel, copied here so the metrics by branch need no join.</summary>
    public Guid BranchId { get; private set; }

    public Guid? AdvisorId { get; private set; }

    /// <summary>
    /// The user who last assigned or unassigned the advisor, and when. Both are null while nobody has done it (a conversation that has never had an
    /// advisor, or an assignment by the system). An id only: the user's name is not copied here.
    /// </summary>
    public Guid? AdvisorAssignedBy { get; private set; }

    public DateTime? AdvisorAssignedAt { get; private set; }

    public ConversationSource Source { get; private set; }

    /// <summary>For an ad, what identified it (headline, URL or ad id).</summary>
    public string? SourceReference { get; private set; }

    public DateTime StartedAt { get; private set; }

    public DateTime LastMessageAt { get; private set; }

    public DateTime? FirstInboundAt { get; private set; }

    public DateTime? FirstResponseAt { get; private set; }

    public ConversationStatus Status { get; private set; } = ConversationStatus.Open;

    /// <summary>When the conversation was closed; null exactly while it is open.</summary>
    public DateTime? ClosedAt { get; private set; }

    /// <summary>True while messages arrived that the classifier has not looked at yet. Set by ingestion, cleared by the classification worker.</summary>
    public bool ClassificationDirty { get; private set; }

    /// <summary>When ingestion last asked for a (re)classification; the age of the backlog.</summary>
    public DateTime? ClassificationRequestedAt { get; private set; }

    /// <summary>Consecutive failed classification attempts since the last request. At the configured maximum the worker stops retrying.</summary>
    public int ClassificationFailures { get; private set; }

    public DateTime? ClassificationAttemptedAt { get; private set; }

    /// <summary>
    /// Consecutive LLM answers that could not be used (refused, cut off, or not in the expected shape) since the last request or the last
    /// LLM success. Transport failures (timeouts, rate limits, outages) are not counted here. Independent of <see cref="ClassificationFailures"/>.
    /// </summary>
    public int LlmFailures { get; private set; }

    /// <summary>
    /// The snapshot hash (SHA-256, 64 lower-case hex characters) of the LLM reading that is applied right now; null when what is applied is
    /// not an LLM reading. A claimed conversation whose snapshot still has this hash is finished without calling the model (nothing it
    /// reads changed). Only the digest is stored, never text.
    /// </summary>
    public string? LastLlmSnapshotHash { get; private set; }

    /// <summary>
    /// The LLM read a contact profile (age range, gender) that is not written to the contact yet. Set in the classification commit, cleared
    /// once the profile is written in its own transaction; a crash in between leaves it set and the worker writes it later, without calling
    /// the model again.
    /// </summary>
    public bool ProfilePending { get; private set; }

    /// <summary>With <see cref="ProfilePending"/>: the age range read (null = not stated).</summary>
    public AgeRange? PendingAgeRange { get; private set; }

    /// <summary>With <see cref="ProfilePending"/>: the gender read.</summary>
    public Gender? PendingGender { get; private set; }

    /// <summary>Records the profile to write after the commit. Something must have been read: a stated range or a gender other than undetermined.</summary>
    public void RecordPendingProfile(AgeRange? ageRange, Gender gender)
    {
        if ((ageRange is { } range && !Enum.IsDefined(range)) || !Enum.IsDefined(gender) || (ageRange is null && gender == Gender.Undetermined))
        {
            throw new DomainException("El perfil pendiente del contacto no es válido.");
        }

        ProfilePending = true;
        PendingAgeRange = ageRange;
        PendingGender = gender;
    }

    /// <summary>The pending profile was written (or its contact no longer exists).</summary>
    public void ClearPendingProfile()
    {
        ProfilePending = false;
        PendingAgeRange = null;
        PendingGender = null;
    }

    /// <summary>
    /// True for a conversation of the internal sandbox (docs/SANDBOX.md): it was opened by a simulated delivery. Set at creation, never
    /// changed. The metrics count it only while the company is in sandbox mode, and nothing is ever sent to the provider for it.
    /// </summary>
    public bool IsSandbox { get; private set; }

    // Required by EF Core for materialization.
    private Conversation()
    {
    }

    public static Conversation Start(
        Guid tenantId, Guid contactId, Guid channelId, Guid branchId, DateTime startedAt, ConversationSource source, string? sourceReference = null,
        bool isSandbox = false)
    {
        if (!Enum.IsDefined(source))
        {
            throw new DomainException("El origen de la conversación no es válido.");
        }

        var started = UtcTime.Normalize(startedAt);
        return new Conversation
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador de la empresa es obligatorio."),
            ContactId = TextRules.RequiredId(contactId, "El contacto es obligatorio."),
            ChannelId = TextRules.RequiredId(channelId, "El canal es obligatorio."),
            BranchId = TextRules.RequiredId(branchId, "La sucursal es obligatoria."),
            Source = source,
            SourceReference = TextRules.Optional(sourceReference, SourceReferenceMaxLength, SourceReferenceRuleMessage),
            StartedAt = started,
            LastMessageAt = started,
            IsSandbox = isSandbox,
        };
    }

    /// <param name="advisorId">The advisor to assign.</param>
    /// <param name="assignedBy">The user who does it; null when the system or a seed does.</param>
    /// <param name="assignedAt">When; recorded only with <paramref name="assignedBy"/> or on its own, null leaves the time empty.</param>
    public void AssignAdvisor(Guid advisorId, Guid? assignedBy = null, DateTime? assignedAt = null)
    {
        AdvisorId = TextRules.RequiredId(advisorId, "El asesor es obligatorio.");
        RecordAssignment(assignedBy, assignedAt);
    }

    public void UnassignAdvisor(Guid? unassignedBy = null, DateTime? unassignedAt = null)
    {
        AdvisorId = null;
        RecordAssignment(unassignedBy, unassignedAt);
    }

    private void RecordAssignment(Guid? by, DateTime? at)
    {
        AdvisorAssignedBy = by;
        AdvisorAssignedAt = at is null ? null : UtcTime.Normalize(at.Value);
    }

    /// <summary>
    /// Closes an open conversation. A closed conversation takes no more messages: the next one from the customer opens a new conversation.
    /// The closing time cannot precede the last message.
    /// </summary>
    public void Close(DateTime closedAt)
    {
        if (Status == ConversationStatus.Closed)
        {
            throw new DomainException("La conversación ya está cerrada.");
        }

        var at = UtcTime.Normalize(closedAt);
        if (at < LastMessageAt)
        {
            throw new DomainException("La fecha de cierre no puede ser anterior al último mensaje de la conversación.");
        }

        Status = ConversationStatus.Closed;
        ClosedAt = at;
    }

    /// <summary>
    /// A referral on any message of the conversation marks it as coming from an ad. A conversation that is already an ad keeps its
    /// reference, unless it has none yet and this referral brings one.
    /// </summary>
    public void MarkAsAds(string? sourceReference)
    {
        var reference = TextRules.Optional(sourceReference, SourceReferenceMaxLength, SourceReferenceRuleMessage);
        if (Source == ConversationSource.Ads)
        {
            SourceReference ??= reference;
            return;
        }

        Source = ConversationSource.Ads;
        SourceReference = reference;
    }

    /// <summary>
    /// New messages changed what the classifier should read: the conversation waits for the classification worker again, with a fresh
    /// failure count. <see cref="ClassificationRequestedAt"/> changes on every call, so the row is always written, even when it was
    /// already dirty: a worker that is classifying the conversation right now cannot clear this request by mistake (its own write
    /// waits for the row lock this one takes, and this one lands after it).
    /// </summary>
    public void RequestClassification(DateTime now)
    {
        ClassificationDirty = true;
        ClassificationRequestedAt = UtcTime.Normalize(now);
        ClassificationFailures = 0;
        ClassificationAttemptedAt = null;
        LlmFailures = 0;
    }

    /// <summary>
    /// The worker classified the conversation: nothing is pending. <see cref="LlmFailures"/> is left alone (a rules reading, or one where the
    /// LLM was not admitted, says nothing about the LLM).
    /// </summary>
    public void CompleteClassification(DateTime now)
    {
        ClassificationDirty = false;
        ClassificationFailures = 0;
        ClassificationAttemptedAt = UtcTime.Normalize(now);
        LastLlmSnapshotHash = null;
    }

    /// <summary>The LLM read the conversation and its reading was applied: nothing is pending and the LLM failures start over.</summary>
    /// <param name="snapshotHash">The hash of the snapshot the reading came from (<see cref="LastLlmSnapshotHash"/>); null forgets it.</param>
    public void CompleteLlmClassification(DateTime now, string? snapshotHash = null)
    {
        if (snapshotHash is not null && (snapshotHash.Length != SnapshotHashLength || !snapshotHash.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')))
        {
            throw new DomainException("La huella de la conversación no es válida.");
        }

        CompleteClassification(now);
        LlmFailures = 0;
        LastLlmSnapshotHash = snapshotHash;
    }

    /// <summary>Length of <see cref="LastLlmSnapshotHash"/>: SHA-256 as lower-case hex.</summary>
    public const int SnapshotHashLength = 64;

    /// <summary>
    /// The rules reading was applied because the LLM failed or was deferred. <paramref name="countAsLlmFailure"/> (an answer that could not be
    /// used) adds one to <see cref="LlmFailures"/>; the conversation stays flagged while <see cref="LlmFailures"/> is at most
    /// <paramref name="maxFallbackRetries"/> (the retries after the first attempt) and is cleared, with the rules standing, beyond it. An
    /// uncounted fallback (transport failure, open breaker) keeps it flagged without counting. <paramref name="attemptedAt"/> may lie in the
    /// future to defer the next claim. Returns whether the conversation is still flagged.
    /// </summary>
    public bool CompleteWithRulesFallback(DateTime attemptedAt, bool countAsLlmFailure, int maxFallbackRetries)
    {
        if (maxFallbackRetries < 0)
        {
            throw new DomainException("El número de reintentos debe ser cero o mayor.");
        }

        ClassificationFailures = 0;
        ClassificationAttemptedAt = UtcTime.Normalize(attemptedAt);
        LastLlmSnapshotHash = null;
        if (countAsLlmFailure)
        {
            LlmFailures++;
        }

        ClassificationDirty = !countAsLlmFailure || LlmFailures <= maxFallbackRetries;
        return ClassificationDirty;
    }

    /// <summary>
    /// Flags the conversation again without resetting any counter or time (the contact profile could not be written after the commit): the
    /// worker reads it again once the retry delay since <see cref="ClassificationAttemptedAt"/> has passed. The stored snapshot hash is
    /// forgotten, so that read calls the model again instead of skipping it.
    /// </summary>
    public void RequeueClassification()
    {
        ClassificationDirty = true;
        LastLlmSnapshotHash = null;
    }

    public void ApplyTimeline(ConversationTimeline timeline)
    {
        StartedAt = UtcTime.Normalize(timeline.StartedAt);
        LastMessageAt = UtcTime.Normalize(timeline.LastMessageAt);
        FirstInboundAt = UtcTime.Normalize(timeline.FirstInboundAt);
        FirstResponseAt = UtcTime.Normalize(timeline.FirstResponseAt);
    }
}
