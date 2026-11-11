namespace Conversia.Web.Services;

/// <summary>
/// Outcome of a conversation write, carried across the post/redirect/get as a fixed code so that no posted value is ever echoed
/// back through the query string.
/// </summary>
public static class ConversationsNotice
{
    public const string AdvisorChanged = "advisor";
    public const string OutcomeSaved = "outcome";
    public const string MessageSent = "sent";
    public const string CustomerMessageQueued = "customer-sent";
    public const string SandboxConversationQueued = "sandbox-started";

    /// <summary>The list, with the notice of a simulated conversation that was just put in the queue (it has no page of its own yet).</summary>
    public static string ListUrl(string code) => $"/conversaciones?notice={code}";

    public static string Url(Guid conversationId, string code) => $"{ConversationLinks.Detail(conversationId)}?notice={code}";

    public static string? Describe(string? code) => code switch
    {
        AdvisorChanged => "Asesor actualizado.",
        OutcomeSaved => "Resultado guardado. El clasificador ya no lo cambiará.",
        MessageSent => "Mensaje enviado.",
        CustomerMessageQueued => "Mensaje del cliente simulado en cola. Aparece en el chat en unos segundos: recarga la página si aún no lo ves.",
        SandboxConversationQueued => "Conversación simulada en cola. Aparece en esta lista en unos segundos: recarga la página si aún no la ves.",
        _ => null,
    };
}
