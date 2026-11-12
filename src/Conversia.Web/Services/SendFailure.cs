namespace Conversia.Web.Services;

/// <summary>
/// What a person reads when <c>POST /api/conversations/{id}/messages</c> refuses or fails, by the machine code of the API's answer
/// (docs/MESSAGING.md). The page words each one itself: the wording says what to do next, which differs when the message may have
/// left, when it did leave, and when it certainly did not.
/// </summary>
public static class SendFailure
{
    public const string ConversationClosed = "conversation_closed";
    public const string ChannelInactive = "channel_inactive";
    public const string CredentialsMissing = "provider_credentials_missing";
    public const string CredentialsInvalid = "provider_credentials_invalid";
    public const string RecipientNotAllowed = "recipient_not_allowed";
    public const string ProviderRejected = "provider_rejected";
    public const string ProviderLimitReached = "provider_limit_reached";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string CredentialsUnavailable = "provider_credentials_unavailable";
    public const string SentNotRecorded = "message_sent_not_recorded";
    public const string SandboxConversation = "sandbox_conversation";
    public const string SandboxModeActive = "sandbox_mode_active";
    public const string SandboxDataDeleted = "sandbox_data_deleted";

    public const string CredentialsUnavailableMessage =
        "Las credenciales del proveedor no están disponibles en este momento. No depende de tu cuenta: contacta al equipo de Conversia.";

    /// <summary>
    /// The send got no answer the Web can use (it timed out, the connection dropped, a gateway or the API failed). The request may have
    /// reached the provider all the same, so this reads like <see cref="ProviderUnavailable"/>: unknown, check before sending again.
    /// </summary>
    public const string OutcomeUnknown =
        "No se recibió una respuesta del servicio y no se sabe si el mensaje salió. Verifica en el teléfono de la línea si el mensaje llegó antes de enviarlo de nuevo.";

    /// <summary>The codes <c>POST /api/conversations/{id}/messages</c> may answer with. The API client carries these and no other.</summary>
    public static readonly IReadOnlySet<string> SendCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        ConversationClosed, ChannelInactive, CredentialsMissing, CredentialsInvalid, RecipientNotAllowed, ProviderRejected,
        ProviderLimitReached, ProviderUnavailable, CredentialsUnavailable, SentNotRecorded, SandboxConversation, SandboxModeActive,
        SandboxDataDeleted,
    };

    /// <summary>The one code the provider-credentials endpoints may answer with: the installation has no usable master key.</summary>
    public static readonly IReadOnlySet<string> CredentialCodes = new HashSet<string>(StringComparer.Ordinal) { CredentialsUnavailable };

    public const string AlreadySubmitted =
        "Este mensaje ya se envió desde este formulario. Revisa la conversación antes de escribirlo otra vez.";

    /// <summary>The message for a code; null for a code this page does not know (the caller then falls back to the generic wording).</summary>
    public static string? Describe(string? code) => code switch
    {
        ConversationClosed => "La conversación está cerrada y ya no admite mensajes.",
        ChannelInactive => "El número de WhatsApp de esta conversación está desactivado. Actívalo en Organización para poder enviar mensajes.",
        CredentialsMissing => "El número de WhatsApp de esta conversación no tiene clave de API del proveedor. Configúrala en Organización para poder enviar mensajes.",
        CredentialsInvalid => "360dialog no reconoce la clave de API de este número. Revísala o reemplázala en Organización; en el sandbox, la clave cambia cada vez que se pide una nueva.",
        RecipientNotAllowed => "360dialog no permite escribirle a este cliente desde este número. En el sandbox solo se puede escribir al número vinculado a la clave.",
        ProviderRejected => "360dialog no aceptó el mensaje. Una causa frecuente es que hayan pasado más de 24 horas desde el último mensaje del cliente.",
        ProviderLimitReached => "Se alcanzó el límite de envíos de 360dialog para la clave de este número. El sandbox de 360dialog permite 25 solicitudes en total por clave, y las rechazadas también cuentan: para seguir probando se necesita una clave nueva.",
        ProviderUnavailable => "360dialog no respondió o falló, y no se sabe si el mensaje salió. Verifica en el teléfono de la línea si el mensaje llegó antes de enviarlo de nuevo.",
        CredentialsUnavailable => CredentialsUnavailableMessage,
        SentNotRecorded => "El mensaje se envió al cliente, pero no se pudo registrar en esta conversación. No lo envíes de nuevo.",
        SandboxConversation => "Esta conversación es de prueba (sandbox) y solo admite mensajes mientras el modo sandbox de tu empresa está activo.",
        SandboxDataDeleted => "Esta conversación de prueba se eliminó mientras escribías. El mensaje no se guardó ni se envió a ningún teléfono.",
        SandboxModeActive => "Tu empresa está en modo sandbox y no se envían mensajes reales. Esta conversación es real: podrás escribir en ella cuando el modo sandbox se desactive.",
        _ => null,
    };

    /// <summary>True when the customer already has the message: the typed text must not stay in the box, ready to be sent a second time.</summary>
    public static bool MessageLeft(string? code) => code == SentNotRecorded;

    /// <summary>Conversia's own limit of sends per minute (a 429 without a code), which is not the provider's quota.</summary>
    public static string TooManyMessages(int? retryAfterSeconds)
        => retryAfterSeconds is { } seconds
            ? $"Se enviaron demasiados mensajes en poco tiempo. Vuelve a intentarlo en {UiMessages.Seconds(seconds)}."
            : "Se enviaron demasiados mensajes en poco tiempo. Vuelve a intentarlo más tarde.";
}
