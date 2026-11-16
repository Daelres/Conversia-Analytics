using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// One WhatsApp message. <c>(ChannelId, ProviderMessageId)</c> is unique, which is what makes ingestion idempotent.
/// <see cref="Text"/> is null for media and other messages without text.
/// </summary>
public class Message : BaseEntity
{
    public const int ProviderMessageIdMaxLength = 200;
    public const int TypeMaxLength = 30;
    public const int TextMaxLength = 4096;
    public const string SenderRuleMessage = "El remitente del mensaje no corresponde con su dirección.";
    public const string MediaRuleMessage = "Los datos del archivo adjunto no son válidos.";

    public static readonly string ProviderMessageIdRuleMessage = $"El identificador del mensaje {TextRules.RuleText(ProviderMessageIdMaxLength)}.";

    public static readonly string TypeRuleMessage = $"El tipo de mensaje debe tener entre 1 y {TypeMaxLength} caracteres, formados por letras minúsculas, dígitos o '_'.";

    public static readonly string TextRuleMessage = $"El texto del mensaje {TextRules.RuleText(TextMaxLength, required: false)}.";

    public Guid TenantId { get; private set; }

    public Guid ConversationId { get; private set; }

    public Guid ChannelId { get; private set; }

    /// <summary>The id the provider gave the message (case-sensitive, kept as received).</summary>
    public string ProviderMessageId { get; private set; } = string.Empty;

    public MessageDirection Direction { get; private set; }

    /// <summary>The provider's message type in lower case: <c>text</c>, <c>image</c>, <c>audio</c>, ...</summary>
    public string Type { get; private set; } = string.Empty;

    public string? Text { get; private set; }

    public DateTime SentAt { get; private set; }

    /// <summary>Who sent it (docs/MESSAGING.md, "Who sent it"): the customer, a Conversia user, the WhatsApp Business app, or unknown.</summary>
    public MessageSender Sender { get; private set; }

    /// <summary>The platform user who sent it from Conversia; null otherwise. An id only.</summary>
    public Guid? SentByUserId { get; private set; }

    /// <summary>
    /// The advisor who sent it, when that is known as a fact: a send from Conversia by a user linked to an advisor. Null otherwise, and
    /// always null for an echo of the WhatsApp Business app (the app does not say who typed it).
    /// </summary>
    public Guid? SentByAdvisorId { get; private set; }

    /// <summary>
    /// For an echo of the WhatsApp Business app (<see cref="MessageSender.PhoneApp"/>): the advisor assigned to the conversation when the
    /// echo arrived. Context, NOT authorship: whoever held the phone may have typed it (docs/decisiones.md). Null otherwise.
    /// </summary>
    public Guid? AssignedAdvisorIdAtReceipt { get; private set; }

    /// <summary>The provider's id of the media (image, audio, video, document, sticker); the file itself is not downloaded.</summary>
    public string? MediaId { get; private set; }

    /// <summary>The media's MIME type as the provider gave it (<c>image/jpeg</c>), lower case.</summary>
    public string? MediaMimeType { get; private set; }

    /// <summary>The media came with a caption (the caption itself is <see cref="Text"/>).</summary>
    public bool HasCaption { get; private set; }

    // Required by EF Core for materialization.
    private Message()
    {
    }

    public static Message Create(
        Guid tenantId, Guid conversationId, Guid channelId, string providerMessageId,
        MessageDirection direction, string type, string? text, DateTime sentAt,
        MessageSender? sender = null, Guid? sentByUserId = null, Guid? sentByAdvisorId = null, MessageMedia? media = null,
        Guid? assignedAdvisorIdAtReceipt = null)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new DomainException("La dirección del mensaje no es válida.");
        }

        if (!IsValidType(type))
        {
            throw new DomainException(TypeRuleMessage);
        }

        // An inbound message is always the customer's; an outbound one never is. Without a sender, the direction decides.
        var who = sender ?? (direction == MessageDirection.Inbound ? MessageSender.Customer : MessageSender.Unknown);
        if (!Enum.IsDefined(who) || (direction == MessageDirection.Inbound) != (who == MessageSender.Customer))
        {
            throw new DomainException(SenderRuleMessage);
        }

        // Who sent it is recorded only as a fact (a Conversia send); the assigned advisor is context of a phone-app echo only.
        if (who != MessageSender.Conversia && (sentByUserId is not null || sentByAdvisorId is not null))
        {
            throw new DomainException(SenderRuleMessage);
        }

        if (who != MessageSender.PhoneApp && assignedAdvisorIdAtReceipt is not null)
        {
            throw new DomainException(SenderRuleMessage);
        }

        if (media is not null && !media.IsValid)
        {
            throw new DomainException(MediaRuleMessage);
        }

        return new Message
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador de la empresa es obligatorio."),
            ConversationId = TextRules.RequiredId(conversationId, "La conversación es obligatoria."),
            ChannelId = TextRules.RequiredId(channelId, "El canal es obligatorio."),
            ProviderMessageId = TextRules.Required(providerMessageId, ProviderMessageIdMaxLength, ProviderMessageIdRuleMessage),
            Direction = direction,
            Type = type,
            // The text is kept as received (leading and trailing spaces can matter); only NUL and the length are refused.
            Text = text is null ? null : IsValidText(text) ? text : throw new DomainException(TextRuleMessage),
            SentAt = UtcTime.Normalize(sentAt),
            Sender = who,
            SentByUserId = OptionalId(sentByUserId),
            SentByAdvisorId = OptionalId(sentByAdvisorId),
            AssignedAdvisorIdAtReceipt = OptionalId(assignedAdvisorIdAtReceipt),
            MediaId = media?.ProviderMediaId,
            MediaMimeType = media?.MimeType,
            HasCaption = media?.HasCaption ?? false,
        };
    }

    /// <summary>
    /// The message was sent from Conversia by <paramref name="userId"/>: used when its echo was stored first (the send path finds it
    /// stored and claims it). Only an outbound message can be claimed.
    /// </summary>
    public void AttributeToConversia(Guid? userId, Guid? advisorId)
    {
        if (Direction != MessageDirection.Outbound)
        {
            throw new DomainException(SenderRuleMessage);
        }

        Sender = MessageSender.Conversia;
        SentByUserId = OptionalId(userId);
        SentByAdvisorId = OptionalId(advisorId);
        AssignedAdvisorIdAtReceipt = null; // it was not an echo of the phone app after all
    }

    private static Guid? OptionalId(Guid? id) => id == Guid.Empty ? throw new DomainException("El identificador no es válido.") : id;

    public static bool IsValidType(string? type)
        => type is { Length: >= 1 and <= TypeMaxLength } && type.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    public static bool IsValidText(string? text)
        => text is null || (text.Length <= TextMaxLength && !text.Contains('\0'));
}

/// <summary>
/// What the webhook says about a media message, without the file (docs/WEBHOOKS.md, "Media metadata"). The file size is not in the webhook
/// (it comes from the media endpoint, roadmap stage 3) and the file name of a document is not kept (it can carry personal data).
/// </summary>
/// <param name="ProviderMediaId">The provider's media id; at most <see cref="MaxIdLength"/> characters.</param>
/// <param name="MimeType">Lower-case <c>type/subtype</c>, at most <see cref="MaxMimeTypeLength"/> characters.</param>
public sealed record MessageMedia(string? ProviderMediaId, string? MimeType, bool HasCaption)
{
    public const int MaxIdLength = 200;
    public const int MaxMimeTypeLength = 100;

    public bool IsValid
        => (ProviderMediaId is null || (ProviderMediaId.Length is >= 1 and <= MaxIdLength && !ProviderMediaId.Contains('\0') && ProviderMediaId.Trim() == ProviderMediaId))
           && (MimeType is null || IsValidMimeType(MimeType));

    public static bool IsValidMimeType(string? mime)
        => mime is { Length: >= 3 and <= MaxMimeTypeLength }
           && mime.Count(c => c == '/') == 1 && mime[0] != '/' && mime[^1] != '/'
           && mime.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '/' or '.' or '+' or '-' or '_');
}
