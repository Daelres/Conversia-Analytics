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

    // Required by EF Core for materialization.
    private Conversation()
    {
    }

    public static Conversation Start(
        Guid tenantId, Guid contactId, Guid channelId, Guid branchId, DateTime startedAt, ConversationSource source, string? sourceReference = null)
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
    }

    /// <summary>The worker classified the conversation: nothing is pending.</summary>
    public void CompleteClassification(DateTime now)
    {
        ClassificationDirty = false;
        ClassificationFailures = 0;
        ClassificationAttemptedAt = UtcTime.Normalize(now);
    }

    public void ApplyTimeline(ConversationTimeline timeline)
    {
        StartedAt = UtcTime.Normalize(timeline.StartedAt);
        LastMessageAt = UtcTime.Normalize(timeline.LastMessageAt);
        FirstInboundAt = UtcTime.Normalize(timeline.FirstInboundAt);
        FirstResponseAt = UtcTime.Normalize(timeline.FirstResponseAt);
    }
}
