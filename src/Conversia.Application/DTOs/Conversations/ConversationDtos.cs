using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Conversations;

/// <summary>
/// Query string of <c>GET /api/conversations</c>. The enum filters are the member names (<c>Won</c>, <c>Ads</c>, <c>Open</c>), never numbers.
/// <c>From</c>/<c>To</c> are local dates of the tenant's time zone (<c>Metrics:TimeZone</c>) applied to the day the conversation
/// started: <c>From</c> inclusive, <c>To</c> exclusive.
/// </summary>
public class ConversationQuery : IValidatableObject
{
    public const int MaxPageSize = 100;

    [Display(Name = "Página")]
    [Range(1, int.MaxValue, ErrorMessage = ValidationMessages.Range)]
    public int Page { get; set; } = 1;

    [Display(Name = "Tamaño de página")]
    [Range(1, MaxPageSize, ErrorMessage = ValidationMessages.Range)]
    public int PageSize { get; set; } = 20;

    [Display(Name = "Sucursal")]
    public Guid? BranchId { get; set; }

    [Display(Name = "Asesor")]
    public Guid? AdvisorId { get; set; }

    /// <summary>Only conversations with no advisor. Cannot be combined with <see cref="AdvisorId"/>.</summary>
    [Display(Name = "Sin asesor")]
    public bool? Unassigned { get; set; }

    [Display(Name = "Etapa")]
    [EnumName(typeof(OpportunityStage))]
    public string? Stage { get; set; }

    [Display(Name = "Origen")]
    [EnumName(typeof(ConversationSource))]
    public string? Source { get; set; }

    [Display(Name = "Estado")]
    [EnumName(typeof(ConversationStatus))]
    public string? Status { get; set; }

    [Display(Name = "Desde")]
    public DateOnly? From { get; set; }

    [Display(Name = "Hasta")]
    public DateOnly? To { get; set; }

    /// <summary>
    /// The customer wrote, nobody has answered and the conversation is still <b>open</b>: somebody can still answer it (the same set as
    /// <c>unansweredOpen</c> of the metrics). Use <see cref="ClosedUnanswered"/> for the ones that closed without a reply.
    /// </summary>
    [Display(Name = "Sin responder")]
    public bool? Unanswered { get; set; }

    /// <summary>The customer wrote and the conversation closed without any reply (the same set as <c>unansweredClosed</c> of the metrics).</summary>
    [Display(Name = "Cerradas sin responder")]
    public bool? ClosedUnanswered { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var fromProblem = DateWindow.Check(From, "Desde", nameof(From), validationContext);
        var toProblem = DateWindow.Check(To, "Hasta", nameof(To), validationContext);
        if (fromProblem is not null)
        {
            yield return fromProblem;
        }

        if (toProblem is not null)
        {
            yield return toProblem;
        }

        if (fromProblem is null && toProblem is null && From is { } from && To is { } to && from >= to)
        {
            yield return new ValidationResult("El campo Desde debe ser anterior al campo Hasta.", [nameof(From), nameof(To)]);
        }

        if (AdvisorId is not null && Unassigned == true)
        {
            yield return new ValidationResult("No se puede filtrar por un asesor y por conversaciones sin asesor a la vez.", [nameof(AdvisorId), nameof(Unassigned)]);
        }

        if (Unanswered == true && ClosedUnanswered == true)
        {
            yield return new ValidationResult("No se puede filtrar por conversaciones abiertas y cerradas sin responder a la vez.", [nameof(Unanswered), nameof(ClosedUnanswered)]);
        }
    }
}

/// <summary>The WhatsApp customer of a conversation. Their number and name are the tenant's own data.</summary>
public class ContactSummary
{
    public Guid Id { get; set; }

    public string WaId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }
}

public class ConversationListItem
{
    public Guid Id { get; set; }

    public ContactSummary Contact { get; set; } = new();

    public Guid ChannelId { get; set; }

    public Guid BranchId { get; set; }

    public string? BranchName { get; set; }

    public Guid? AdvisorId { get; set; }

    public string? AdvisorName { get; set; }

    public string Source { get; set; } = string.Empty;

    public string? SourceReference { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    public DateTime LastMessageAt { get; set; }

    public DateTime? FirstInboundAt { get; set; }

    public DateTime? FirstResponseAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    /// <summary>The customer wrote, nobody has answered and the conversation is still open.</summary>
    public bool Unanswered => FirstInboundAt is not null && FirstResponseAt is null && Status == nameof(ConversationStatus.Open);

    /// <summary>The customer wrote and the conversation closed without any reply.</summary>
    public bool ClosedUnanswered => FirstInboundAt is not null && FirstResponseAt is null && Status == nameof(ConversationStatus.Closed);

    /// <summary>The customer's latest message: WhatsApp's 24-hour customer service window runs from it (docs/MESSAGING.md).</summary>
    public DateTime? LastInboundAt { get; set; }

    /// <summary>The business's latest message, by any way of sending.</summary>
    public DateTime? LastOutboundAt { get; set; }

    /// <summary>The customer wrote last: nothing from the business at or after the customer's latest message.</summary>
    public bool AwaitingReply => LastInboundAt is { } inbound && (LastOutboundAt is not { } outbound || outbound < inbound);

    /// <summary>When the customer service window closes (24 hours after <see cref="LastInboundAt"/>); null when the customer never wrote.</summary>
    public DateTime? WindowClosesAt => CustomerServiceWindow.ClosesAt(LastInboundAt);

    /// <summary>Whether free text can be sent now, as of when the API answered. Outside the window only a template can be sent.</summary>
    public bool WindowOpen { get; set; }

    /// <summary>The window is open and closes within two hours.</summary>
    public bool WindowClosingSoon { get; set; }

    public int MessageCount { get; set; }

    /// <summary>True for a conversation of the internal sandbox (docs/SANDBOX.md): a simulated customer, never a real one.</summary>
    public bool IsSandbox { get; set; }

    public OpportunityResponse Opportunity { get; set; } = new();
}

/// <summary>
/// What the detail of a conversation says about its WhatsApp channel: only whether a message can leave through it, so that a person who
/// may write (<c>ConversationsManage</c>) is told why not without needing to see the organization. Nothing that identifies or
/// authenticates the channel is here: no number, no webhook key, no provider key.
/// </summary>
public class ConversationChannelState
{
    public bool IsActive { get; set; }

    /// <summary>Whether a provider API key is stored for the channel. The key itself is never returned.</summary>
    public bool HasProviderApiKey { get; set; }

    /// <summary><c>Sandbox</c> or <c>Production</c>.</summary>
    public string ProviderEnvironment { get; set; } = string.Empty;
}

public class ConversationDetailResponse : ConversationListItem
{
    /// <summary>Whether the channel of the conversation can send; null only from an API older than this field.</summary>
    public ConversationChannelState? Channel { get; set; }

    /// <summary>The user who last assigned or unassigned the advisor (an id only, no name); null when nobody has, or the system did.</summary>
    public Guid? AdvisorAssignedBy { get; set; }

    public DateTime? AdvisorAssignedAt { get; set; }

    /// <summary>When the AI last updated this conversation's fields (its scheduled analysis); null when it never did.</summary>
    public DateTime? AiAnalyzedAt { get; set; }

    /// <summary>Oldest first, at most <see cref="MaxMessages"/>; <see cref="ConversationListItem.MessageCount"/> says how many exist.</summary>
    public IReadOnlyList<MessageResponse> Messages { get; set; } = [];

    public const int MaxMessages = 2000;
}

public class MessageResponse
{
    public Guid Id { get; set; }

    /// <summary><c>Inbound</c> (the customer) or <c>Outbound</c> (the business).</summary>
    public string Direction { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    /// <summary>Null for media without caption.</summary>
    public string? Text { get; set; }

    public DateTime SentAt { get; set; }

    /// <summary>The template a send from Conversia used; null for any other message.</summary>
    public string? TemplateName { get; set; }

    /// <summary><c>Marketing</c>, <c>Utility</c> or <c>Authentication</c> for a template message: Meta charges it by this category.</summary>
    public string? TemplateCategory { get; set; }

    /// <summary>The message's media file, when it has one (docs/MESSAGING.md, "Media"): its state and type, never the provider's id nor the store's key.</summary>
    public MessageAttachmentResponse? Attachment { get; set; }

    /// <summary>The message came from an imported chat history (coexistence onboarding).</summary>
    public bool Imported { get; set; }

    /// <summary>Who wrote it (docs/MESSAGING.md, "Who sent it"): <c>Customer</c>, <c>Conversia</c>, <c>PhoneApp</c>, <c>AiAssistant</c>, <c>Unknown</c>.</summary>
    public string Sender { get; set; } = string.Empty;

    public static MessageResponse From(Message message) => new()
    {
        Sender = message.Sender.ToString(),
        Id = message.Id,
        Direction = message.Direction.ToString(),
        Type = message.Type,
        Text = message.Text,
        SentAt = message.SentAt,
        TemplateName = message.TemplateName,
        TemplateCategory = message.TemplateCategory?.ToString(),
        Attachment = MessageAttachmentResponse.From(message),
        Imported = message.IsImported,
    };
}

/// <summary>The media file of a message. Served by <c>GET /api/conversations/{id}/messages/{messageId}/media</c> once <see cref="Status"/> is <c>stored</c>.</summary>
public class MessageAttachmentResponse
{
    /// <summary><c>pending</c>, <c>stored</c>, <c>too_large</c>, <c>blocked</c>, <c>expired</c>, <c>failed</c> or <c>purged</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary><c>image</c>, <c>audio</c>, <c>video</c>, <c>document</c> or <c>sticker</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The type it is served with (checked against its content); null until stored.</summary>
    public string? ContentType { get; set; }

    public long? SizeBytes { get; set; }

    public static MessageAttachmentResponse? From(Message message)
        => message.MediaStatus is not { } status
            ? null
            : new MessageAttachmentResponse
            {
                Status = StatusName(status),
                Kind = message.Type,
                ContentType = status == MediaStatus.Stored ? message.MediaContentType : null,
                SizeBytes = status == MediaStatus.Stored ? message.MediaSizeBytes : null,
            };

    public static string StatusName(MediaStatus status) => status switch
    {
        MediaStatus.TooLarge => "too_large",
        _ => status.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// An attachment to send (<c>POST /api/conversations/{id}/media-messages</c>, multipart <c>file</c> and <c>caption</c>). The file's name is never
/// read: it can carry personal data and nothing needs it.
/// </summary>
public sealed class SendMediaMessageRequest
{
    /// <summary>WhatsApp's limit of a media caption.</summary>
    public const int CaptionMaxLength = 1024;

    public static readonly string CaptionRuleMessage = $"El texto del adjunto {Domain.Common.TextRules.RuleText(CaptionMaxLength, required: false)}.";

    /// <summary>The file, seekable (the API's buffered form file): it is read from here, never copied whole into memory.</summary>
    public Stream Content { get; init; } = Stream.Null;

    /// <summary>The type the browser declared: a hint only, the content decides.</summary>
    public string? DeclaredContentType { get; init; }

    public string? Caption { get; init; }

    public override string ToString() => $"SendMediaMessageRequest {{ Length = {(Content.CanSeek ? Content.Length : -1)}, HasCaption = {!string.IsNullOrWhiteSpace(Caption)} }}";
}

/// <summary>
/// Body of <c>POST /api/conversations/{id}/messages</c>: a plain-text message a person sends to the customer of the conversation.
/// The text is sent and stored trimmed.
/// </summary>
public class SendMessageRequest
{
    public static readonly string TextRuleMessage = $"El texto del mensaje {Domain.Common.TextRules.RuleText(Message.TextMaxLength)}.";

    [Display(Name = "Texto")]
    [TrimmedText(Message.TextMaxLength)]
    public string? Text { get; set; }
}

/// <summary>
/// Body of <c>POST /api/conversations/{id}/template-messages</c>: an approved template of the conversation's channel, by name and language,
/// with one value per placeholder in the order the template lists them (docs/MESSAGING.md, "Templates").
/// </summary>
public class SendTemplateMessageRequest
{
    [Display(Name = "Plantilla")]
    [TrimmedText(MessageTemplate.NameMaxLength)]
    public string? TemplateName { get; set; }

    [Display(Name = "Idioma")]
    [TrimmedText(MessageTemplate.LanguageMaxLength)]
    public string? Language { get; set; }

    /// <summary>The values of the placeholders; each one is checked against the template (count, length, no line breaks).</summary>
    [Display(Name = "Valores")]
    [MaxLength(MessageTemplate.MaxParameters, ErrorMessage = ValidationMessages.MaxItems)]
    public List<string?> Parameters { get; set; } = [];
}

public class OpportunityResponse
{
    public string Stage { get; set; } = string.Empty;

    /// <summary><c>Auto</c> (the system decided) or <c>Manual</c> (a person did and the classifier no longer changes it).</summary>
    public string StageSource { get; set; } = string.Empty;

    public DateTime? StageChangedAt { get; set; }

    /// <summary>The user who last changed the stage by hand (an id only, no name); null when the system set it (the classifier, the close-by-inactivity rule).</summary>
    public Guid? StageChangedBy { get; set; }

    public string? ServiceInterest { get; set; }

    public string? InterestReason { get; set; }

    public string Objection { get; set; } = string.Empty;

    public string? LossReason { get; set; }

    public string? LossDetail { get; set; }

    public DateTime? ClosedAt { get; set; }

    public DateTime? ClassifiedAt { get; set; }

    /// <summary>How sure the classifier was, 0 to 1; null until the conversation has been classified.</summary>
    public decimal? ClassificationConfidence { get; set; }

    /// <summary>Comma-separated codes of the rules behind the last classification (<c>outcome.lost.refusal,objection.price</c>); never customer text.</summary>
    public string? ClassificationRules { get; set; }

    /// <summary>How far the conversation got (a <c>ReachedStage</c> name, <c>FirstContact</c> ... <c>PaymentOrDeposit</c>); null when not read yet.</summary>
    public string? ReachedStage { get; set; }

    /// <summary><c>None</c>, <c>PhoneCall</c>, <c>InPerson</c> or <c>OtherChannel</c>.</summary>
    public string MovedOffChannel { get; set; } = string.Empty;

    /// <summary>The 1-based position, among the messages the classifier read, of the message that shows the move; null without a move.</summary>
    public int? OffChannelEvidenceIndex { get; set; }

    /// <summary><c>Unknown</c>, <c>Advisor</c>, <c>CustomerSelfService</c> (or <c>AiAssistant</c>, reserved).</summary>
    public string ClosedBy { get; set; } = string.Empty;

    public bool ClosedOutsideSystem { get; set; }

    /// <summary><c>Auto</c> or <c>Manual</c>, per closing-point field.</summary>
    public string ReachedStageSource { get; set; } = string.Empty;

    public string MovedOffChannelSource { get; set; } = string.Empty;

    public string ClosedBySource { get; set; } = string.Empty;

    public string ClosedOutsideSystemSource { get; set; } = string.Empty;

    public static OpportunityResponse From(Opportunity opportunity) => new()
    {
        Stage = opportunity.Stage.ToString(),
        StageSource = opportunity.StageSource.ToString(),
        StageChangedAt = opportunity.StageChangedAt,
        StageChangedBy = opportunity.StageChangedBy,
        ServiceInterest = opportunity.ServiceInterest,
        InterestReason = opportunity.InterestReason,
        Objection = opportunity.Objection.ToString(),
        LossReason = opportunity.LossReason?.ToString(),
        LossDetail = opportunity.LossDetail,
        ClosedAt = opportunity.ClosedAt,
        ClassifiedAt = opportunity.ClassifiedAt,
        ClassificationConfidence = opportunity.ClassificationConfidence,
        ClassificationRules = opportunity.ClassificationRules,
        ReachedStage = opportunity.ReachedStage?.ToString(),
        MovedOffChannel = opportunity.MovedOffChannel.ToString(),
        OffChannelEvidenceIndex = opportunity.OffChannelEvidenceIndex,
        ClosedBy = opportunity.ClosedBy.ToString(),
        ClosedOutsideSystem = opportunity.ClosedOutsideSystem,
        ReachedStageSource = opportunity.ReachedStageSource.ToString(),
        MovedOffChannelSource = opportunity.MovedOffChannelSource.ToString(),
        ClosedBySource = opportunity.ClosedBySource.ToString(),
        ClosedOutsideSystemSource = opportunity.ClosedOutsideSystemSource.ToString(),
    };
}

/// <summary>Body of <c>PUT /api/conversations/{id}/advisor</c>: the advisor to assign, or null to leave the conversation unassigned.</summary>
public class AssignAdvisorRequest
{
    [Display(Name = "Asesor")]
    public Guid? AdvisorId { get; set; }
}

/// <summary>
/// Body of <c>PUT /api/conversations/{id}/opportunity</c>: a person decides the outcome. <c>Stage</c> is <c>Won</c>, <c>Lost</c> or
/// <c>InFollowUp</c>; <c>Lost</c> requires <c>LossReason</c> (a <see cref="Domain.Enums.LossReason"/> name); <c>Detail</c> is optional free text for a loss.
/// </summary>
public class SetOpportunityOutcomeRequest
{
    [Display(Name = "Etapa")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [EnumName(typeof(OpportunityStage))]
    public string? Stage { get; set; }

    [Display(Name = "Motivo de pérdida")]
    [EnumName(typeof(LossReason))]
    public string? LossReason { get; set; }

    [Display(Name = "Detalle")]
    [TrimmedText(Opportunity.DetailMaxLength, Required = false)]
    public string? Detail { get; set; }
}
