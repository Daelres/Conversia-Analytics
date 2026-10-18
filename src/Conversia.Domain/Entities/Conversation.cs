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
            TenantId = TextRules.RequiredId(tenantId, "El identificador del espacio de trabajo es obligatorio."),
            ContactId = TextRules.RequiredId(contactId, "El contacto es obligatorio."),
            ChannelId = TextRules.RequiredId(channelId, "El canal es obligatorio."),
            BranchId = TextRules.RequiredId(branchId, "La sucursal es obligatoria."),
            Source = source,
            SourceReference = TextRules.Optional(sourceReference, SourceReferenceMaxLength, SourceReferenceRuleMessage),
            StartedAt = started,
            LastMessageAt = started,
        };
    }

    public void AssignAdvisor(Guid advisorId) => AdvisorId = TextRules.RequiredId(advisorId, "El asesor es obligatorio.");

    public void UnassignAdvisor() => AdvisorId = null;

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
    /// Opens a closed conversation again. Not used by ingestion (a message after the close starts a new conversation, see docs/WEBHOOKS.md);
    /// it exists for a person who closed one by mistake. The database allows one open conversation per contact and channel, so reopening
    /// while the contact has another open one fails there.
    /// </summary>
    public void Reopen()
    {
        if (Status == ConversationStatus.Open)
        {
            throw new DomainException("La conversación ya está abierta.");
        }

        Status = ConversationStatus.Open;
        ClosedAt = null;
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

    public void ApplyTimeline(ConversationTimeline timeline)
    {
        StartedAt = UtcTime.Normalize(timeline.StartedAt);
        LastMessageAt = UtcTime.Normalize(timeline.LastMessageAt);
        FirstInboundAt = UtcTime.Normalize(timeline.FirstInboundAt);
        FirstResponseAt = UtcTime.Normalize(timeline.FirstResponseAt);
    }
}
