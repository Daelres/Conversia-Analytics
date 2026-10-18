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

    // Required by EF Core for materialization.
    private Message()
    {
    }

    public static Message Create(
        Guid tenantId, Guid conversationId, Guid channelId, string providerMessageId,
        MessageDirection direction, string type, string? text, DateTime sentAt)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new DomainException("La dirección del mensaje no es válida.");
        }

        if (!IsValidType(type))
        {
            throw new DomainException(TypeRuleMessage);
        }

        return new Message
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador del espacio de trabajo es obligatorio."),
            ConversationId = TextRules.RequiredId(conversationId, "La conversación es obligatoria."),
            ChannelId = TextRules.RequiredId(channelId, "El canal es obligatorio."),
            ProviderMessageId = TextRules.Required(providerMessageId, ProviderMessageIdMaxLength, ProviderMessageIdRuleMessage),
            Direction = direction,
            Type = type,
            // The text is kept as received (leading and trailing spaces can matter); only NUL and the length are refused.
            Text = text is null ? null : IsValidText(text) ? text : throw new DomainException(TextRuleMessage),
            SentAt = UtcTime.Normalize(sentAt),
        };
    }

    public static bool IsValidType(string? type)
        => type is { Length: >= 1 and <= TypeMaxLength } && type.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    public static bool IsValidText(string? text)
        => text is null || (text.Length <= TextMaxLength && !text.Contains('\0'));
}
