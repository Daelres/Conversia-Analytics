namespace Conversia.Web.Services;

/// <summary>
/// What a person reads when the simulated customer of the internal sandbox is refused, by the machine code of the API's answer
/// (docs/SANDBOX.md). Nothing was sent anywhere in any of these: the simulated customer only ever writes into Conversia.
/// </summary>
public static class SandboxFailure
{
    public const string ModeOff = "sandbox_mode_off";
    public const string NotSandboxConversation = "not_sandbox_conversation";
    public const string ConversationClosed = "conversation_closed";
    public const string ChannelInactive = "channel_inactive";

    /// <summary>The codes the simulated-customer endpoints may answer with. The API client carries these and no other.</summary>
    public static readonly IReadOnlySet<string> Codes = new HashSet<string>(StringComparer.Ordinal)
    {
        ModeOff, NotSandboxConversation, ConversationClosed, ChannelInactive,
    };

    public const string AlreadySubmitted = "Este mensaje ya se envió desde este formulario. Revisa la conversación antes de escribirlo otra vez.";

    /// <summary>The message for a code; null for a code this page does not know (the caller then falls back to the generic wording).</summary>
    public static string? Describe(string? code) => code switch
    {
        ModeOff => "El modo sandbox no está activo para tu empresa. Solo el equipo de Conversia puede activarlo.",
        NotSandboxConversation => "Esta conversación es real: el cliente simulado solo puede escribir en conversaciones de prueba.",
        ConversationClosed => "La conversación está cerrada y ya no admite mensajes. Inicia una nueva conversación simulada.",
        ChannelInactive => "El número de WhatsApp está desactivado. Actívalo en Organización para simular mensajes en él.",
        _ => null,
    };

    /// <summary>Conversia's own limit of messages per minute, which the simulated customer shares with sending.</summary>
    public static string TooManyMessages(int? retryAfterSeconds) => SendFailure.TooManyMessages(retryAfterSeconds);
}
