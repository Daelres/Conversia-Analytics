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

    /// <summary>The template a Conversia send used (docs/MESSAGING.md, "Templates"); null for any other message.</summary>
    public string? TemplateName { get; private set; }

    /// <summary>The category of <see cref="TemplateName"/> when it was sent: Meta charges a template message by it.</summary>
    public TemplateCategory? TemplateCategory { get; private set; }

    /// <summary>Where the stored copy of the media stands; null for a message without media (docs/MESSAGING.md, "Media").</summary>
    public MediaStatus? MediaStatus { get; private set; }

    /// <summary>The key of the stored copy in the media store (random, under the company's prefix); never the file's name. Never in an API answer.</summary>
    public string? MediaObjectKey { get; private set; }

    /// <summary>The size of the stored copy.</summary>
    public long? MediaSizeBytes { get; private set; }

    /// <summary>The type the stored copy is served with: checked against its content, from the allow-list.</summary>
    public string? MediaContentType { get; private set; }

    /// <summary>Download attempts that failed and are to be retried.</summary>
    public int MediaAttempts { get; private set; }

    /// <summary>When the download job may try next; null once the media reached a final state.</summary>
    public DateTime? MediaNextAttemptAt { get; private set; }

    /// <summary>When the copy was stored (the media retention counts from here).</summary>
    public DateTime? MediaStoredAt { get; private set; }

    /// <summary>The message came from a shared chat history (coexistence onboarding), not live (docs/WEBHOOKS.md, "History import").</summary>
    public bool IsImported { get; private set; }

    // Required by EF Core for materialization.
    private Message()
    {
    }

    public static Message Create(
        Guid tenantId, Guid conversationId, Guid channelId, string providerMessageId,
        MessageDirection direction, string type, string? text, DateTime sentAt,
        MessageSender? sender = null, Guid? sentByUserId = null, Guid? sentByAdvisorId = null, MessageMedia? media = null,
        Guid? assignedAdvisorIdAtReceipt = null, MessageTemplateUse? template = null, bool imported = false, StoredMediaObject? storedMedia = null)
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

        // A template is only ever sent from Conversia; nothing that arrives through the webhook says which template it was.
        if (template is not null && (who != MessageSender.Conversia || !MessageTemplate.IsValidName(template.Name) || !Enum.IsDefined(template.Category)))
        {
            throw new DomainException(SenderRuleMessage);
        }

        if (media is not null && !media.IsValid)
        {
            throw new DomainException(MediaRuleMessage);
        }

        // A stored copy comes only with a send from Conversia (the file was stored before it was sent); anything received waits for the job.
        if (storedMedia is not null && (media is null || who != MessageSender.Conversia || !storedMedia.IsValid))
        {
            throw new DomainException(MediaRuleMessage);
        }

        var sent = UtcTime.Normalize(sentAt);
        var mediaStatus = media is null ? (MediaStatus?)null
            : storedMedia is not null ? Enums.MediaStatus.Stored
            : media.ProviderMediaId is null ? Enums.MediaStatus.Failed
            : Enums.MediaStatus.Pending;

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
            SentAt = sent,
            Sender = who,
            SentByUserId = OptionalId(sentByUserId),
            SentByAdvisorId = OptionalId(sentByAdvisorId),
            AssignedAdvisorIdAtReceipt = OptionalId(assignedAdvisorIdAtReceipt),
            MediaId = media?.ProviderMediaId,
            MediaMimeType = media?.MimeType,
            HasCaption = media?.HasCaption ?? false,
            TemplateName = template?.Name,
            TemplateCategory = template?.Category,
            MediaStatus = mediaStatus,
            MediaNextAttemptAt = mediaStatus == Enums.MediaStatus.Pending ? sent : null,
            MediaObjectKey = storedMedia?.ObjectKey,
            MediaSizeBytes = storedMedia?.SizeBytes,
            MediaContentType = storedMedia?.ContentType,
            MediaStoredAt = storedMedia is null ? null : sent,
            IsImported = imported,
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

    /// <summary>The message was sent by the AI sales assistant (docs/ASSISTANT.md): its echo was stored first. No user, no advisor.</summary>
    public void AttributeToAssistant()
    {
        if (Direction != MessageDirection.Outbound)
        {
            throw new DomainException(SenderRuleMessage);
        }

        Sender = MessageSender.AiAssistant;
        SentByUserId = null;
        SentByAdvisorId = null;
        AssignedAdvisorIdAtReceipt = null;
    }

    /// <summary>The download job stored a copy of the media.</summary>
    public void MarkMediaStored(StoredMediaObject stored, DateTime at)
    {
        RequirePendingMedia();
        if (!stored.IsValid)
        {
            throw new DomainException(MediaRuleMessage);
        }

        MediaStatus = Enums.MediaStatus.Stored;
        MediaObjectKey = stored.ObjectKey;
        MediaSizeBytes = stored.SizeBytes;
        MediaContentType = stored.ContentType;
        MediaStoredAt = UtcTime.Normalize(at);
        MediaNextAttemptAt = null;
    }

    /// <summary>An attempt failed in a way that may pass (the provider was unavailable): counted, and tried again at <paramref name="nextAttemptAt"/>.</summary>
    public void ScheduleMediaRetry(DateTime nextAttemptAt)
    {
        RequirePendingMedia();
        MediaAttempts++;
        MediaNextAttemptAt = UtcTime.Normalize(nextAttemptAt);
    }

    /// <summary>Moves the next download attempt without counting one (the attempt was counted when it started).</summary>
    public void PostponeMedia(DateTime nextAttemptAt)
    {
        RequirePendingMedia();
        MediaNextAttemptAt = UtcTime.Normalize(nextAttemptAt);
    }

    /// <summary>
    /// The attempt failed on Conversia's side (the media store was unavailable), not the provider's: it is given back and the next one moved,
    /// so an outage of the store never uses up the attempts while the provider still has the file.
    /// </summary>
    public void ReleaseMediaAttempt(DateTime nextAttemptAt)
    {
        RequirePendingMedia();
        MediaAttempts = Math.Max(0, MediaAttempts - 1);
        MediaNextAttemptAt = UtcTime.Normalize(nextAttemptAt);
    }

    /// <summary>The media will never be stored: too large, blocked, expired at the provider, or every attempt failed.</summary>
    public void MarkMediaUnavailable(MediaStatus status)
    {
        if (status is not (Enums.MediaStatus.TooLarge or Enums.MediaStatus.Blocked or Enums.MediaStatus.Expired or Enums.MediaStatus.Failed))
        {
            throw new DomainException(MediaRuleMessage);
        }

        RequirePendingMedia();
        MediaStatus = status;
        MediaNextAttemptAt = null;
    }

    /// <summary>The company's media retention ran out: the copy is forgotten. Returns its key, for the store to delete.</summary>
    public string PurgeMedia()
    {
        if (MediaStatus != Enums.MediaStatus.Stored || MediaObjectKey is not { } key)
        {
            throw new DomainException(MediaRuleMessage);
        }

        MediaStatus = Enums.MediaStatus.Purged;
        MediaObjectKey = null;
        return key;
    }

    /// <summary>
    /// The follow-up of a history <c>media_placeholder</c> brings the media's type and id (Meta's <c>history</c> reference): the stored
    /// placeholder becomes that media message and waits for its download from <paramref name="at"/> (when the follow-up arrived).
    /// </summary>
    public void CompleteHistoryPlaceholder(string type, MessageMedia media, DateTime at)
    {
        if (!IsImported || Type != HistoryPlaceholderType || !IsValidType(type) || !media.IsValid)
        {
            throw new DomainException(MediaRuleMessage);
        }

        Type = type;
        MediaId = media.ProviderMediaId;
        MediaMimeType = media.MimeType;
        HasCaption = media.HasCaption;
        MediaStatus = media.ProviderMediaId is null ? Enums.MediaStatus.Failed : Enums.MediaStatus.Pending;
        MediaNextAttemptAt = media.ProviderMediaId is null ? null : UtcTime.Normalize(at);
    }

    /// <summary>
    /// The send path found this message stored first (its echo arrived before the record): it takes the copy the send stored, unless it already
    /// has one. Returns false when it keeps its own (the caller deletes the other copy).
    /// </summary>
    public bool AdoptStoredCopy(StoredMediaObject stored, DateTime at)
    {
        if (!stored.IsValid)
        {
            throw new DomainException(MediaRuleMessage);
        }

        if (MediaStatus is Enums.MediaStatus.Stored or Enums.MediaStatus.Purged)
        {
            return false;
        }

        MediaStatus = Enums.MediaStatus.Stored;
        MediaObjectKey = stored.ObjectKey;
        MediaSizeBytes = stored.SizeBytes;
        MediaContentType = stored.ContentType;
        MediaStoredAt = UtcTime.Normalize(at);
        MediaNextAttemptAt = null;
        return true;
    }

    /// <summary>The type of a history message whose media is announced later (Meta's <c>history</c> reference).</summary>
    public const string HistoryPlaceholderType = "media_placeholder";

    private void RequirePendingMedia()
    {
        if (MediaStatus != Enums.MediaStatus.Pending)
        {
            throw new DomainException(MediaRuleMessage);
        }
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

/// <summary>A copy of a message's media in the media store: its key, its size and the type it is served with.</summary>
public sealed record StoredMediaObject(string ObjectKey, long SizeBytes, string ContentType)
{
    public const int MaxKeyLength = 200;

    /// <summary>A key is lower-case letters, digits, '-' and '/' (segments never empty, never "." or ".."); the size positive; the type a MIME type.</summary>
    public bool IsValid
        => IsValidKey(ObjectKey) && SizeBytes > 0 && MessageMedia.IsValidMimeType(ContentType);

    public static bool IsValidKey(string? key)
        => key is { Length: >= 1 and <= MaxKeyLength }
           && key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '/')
           && key.Split('/').All(segment => segment.Length > 0);
}
