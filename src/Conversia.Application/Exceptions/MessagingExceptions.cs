namespace Conversia.Application.Exceptions;

/// <summary>Why a message could not be sent (or, for <see cref="SentNotRecorded"/>, recorded). Each one is a distinct answer of the API.</summary>
public enum MessagingProblem
{
    /// <summary>The conversation is closed; it takes no more messages.</summary>
    ConversationClosed,

    /// <summary>The channel of the conversation is switched off.</summary>
    ChannelInactive,

    /// <summary>The channel has no provider API key.</summary>
    CredentialsMissing,

    /// <summary>The provider does not recognize the channel's API key.</summary>
    InvalidCredentials,

    /// <summary>The provider does not let this channel write to this customer.</summary>
    RecipientNotAllowed,

    /// <summary>The provider's quota for the channel's API key is used up.</summary>
    LimitReached,

    /// <summary>The provider refused the message for another reason.</summary>
    Rejected,

    /// <summary>
    /// The provider could not be reached, timed out or failed. Whether the message left is unknown: a person must check before sending it
    /// again, and nothing retries by itself.
    /// </summary>
    ProviderUnavailable,

    /// <summary>The provider accepted the message but it could not be stored: it must not be sent again.</summary>
    SentNotRecorded,

    /// <summary>The conversation is sandbox data and the company is not in sandbox mode: nothing real is ever sent into it.</summary>
    SandboxConversation,

    /// <summary>The company is in sandbox mode and the conversation is real: nothing is sent, and no simulated message is stored in real data.</summary>
    SandboxModeActive,

    /// <summary>
    /// The sandbox conversation was deleted (the company removed its sandbox data) while the message was being sent. Nothing left the
    /// server in sandbox mode, so there is nothing to verify and nothing to keep.
    /// </summary>
    SandboxDataDeleted,
}

/// <summary>
/// A message was not sent, or was sent and not recorded. The API layer maps each <see cref="Problem"/> to its status and puts
/// <see cref="Code"/> in the problem body. The messages are safe to return to the caller: they never quote the provider.
/// </summary>
public class MessagingException : Exception
{
    public MessagingException(MessagingProblem problem, Exception? innerException = null)
        : base(MessageOf(problem), innerException)
    {
        Problem = problem;
    }

    public MessagingProblem Problem { get; }

    /// <summary>Stable machine-readable name of the problem, for example <c>provider_limit_reached</c>.</summary>
    public string Code => CodeOf(Problem);

    public static string CodeOf(MessagingProblem problem) => problem switch
    {
        MessagingProblem.ConversationClosed => "conversation_closed",
        MessagingProblem.ChannelInactive => "channel_inactive",
        MessagingProblem.CredentialsMissing => "provider_credentials_missing",
        MessagingProblem.InvalidCredentials => "provider_credentials_invalid",
        MessagingProblem.RecipientNotAllowed => "recipient_not_allowed",
        MessagingProblem.LimitReached => "provider_limit_reached",
        MessagingProblem.Rejected => "provider_rejected",
        MessagingProblem.ProviderUnavailable => "provider_unavailable",
        MessagingProblem.SentNotRecorded => "message_sent_not_recorded",
        MessagingProblem.SandboxConversation => "sandbox_conversation",
        MessagingProblem.SandboxModeActive => "sandbox_mode_active",
        MessagingProblem.SandboxDataDeleted => "sandbox_data_deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(problem)),
    };

    public static string MessageOf(MessagingProblem problem) => problem switch
    {
        MessagingProblem.ConversationClosed => "La conversación está cerrada y ya no admite mensajes.",
        MessagingProblem.ChannelInactive => "El número de WhatsApp de esta conversación está desactivado.",
        MessagingProblem.CredentialsMissing => "El número de WhatsApp de esta conversación no tiene configurada la clave de API del proveedor.",
        MessagingProblem.InvalidCredentials => "El proveedor de WhatsApp no reconoce la clave de API configurada para este número.",
        MessagingProblem.RecipientNotAllowed => "El proveedor de WhatsApp no permite escribirle a este destinatario desde este número.",
        MessagingProblem.LimitReached => "Se alcanzó el límite de envíos del proveedor de WhatsApp para este número.",
        MessagingProblem.Rejected => "El proveedor de WhatsApp no aceptó el mensaje.",
        MessagingProblem.ProviderUnavailable => "El proveedor de WhatsApp no respondió o falló, y no se sabe si el mensaje salió. Verifica en el teléfono si llegó antes de enviarlo de nuevo.",
        MessagingProblem.SentNotRecorded => "El mensaje se envió, pero no se pudo registrar en la conversación. No lo envíes de nuevo.",
        MessagingProblem.SandboxConversation => "Esta conversación es de prueba (modo sandbox) y solo admite mensajes mientras el modo sandbox está activo.",
        MessagingProblem.SandboxDataDeleted => "Esta conversación de prueba se eliminó mientras se enviaba el mensaje. El mensaje no se guardó ni se envió a ningún teléfono.",
        MessagingProblem.SandboxModeActive => "El modo sandbox está activo y no se envían mensajes reales. Esta conversación es real: no admite mensajes hasta que se desactive el modo sandbox.",
        _ => throw new ArgumentOutOfRangeException(nameof(problem)),
    };
}

public enum SecretProtectionFailure
{
    /// <summary>No master key is configured, so nothing can be protected or read.</summary>
    NotConfigured,

    /// <summary>The stored value was not written with the configured key, was altered, or belongs to something else.</summary>
    Unreadable,
}

/// <summary>
/// A stored secret could not be protected or read. An operations problem, not the caller's: the API layer maps it to 503. The message
/// is fixed text and never describes the secret, the stored value or the key.
/// </summary>
public class SecretProtectionException : Exception
{
    public const string Code = "provider_credentials_unavailable";
    public const string DefaultMessage = "Las credenciales del proveedor no están disponibles en este momento. Contacta al administrador de Conversia.";

    public SecretProtectionException(SecretProtectionFailure failure)
        : base(DefaultMessage)
    {
        Failure = failure;
    }

    public SecretProtectionFailure Failure { get; }
}
