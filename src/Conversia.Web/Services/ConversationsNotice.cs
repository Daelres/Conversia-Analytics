namespace Conversia.Web.Services;

/// <summary>
/// Outcome of a conversation write, carried across the post/redirect/get as a fixed code so that no posted value is ever echoed
/// back through the query string.
/// </summary>
public static class ConversationsNotice
{
    public const string AdvisorChanged = "advisor";
    public const string OutcomeSaved = "outcome";

    public static string Url(Guid conversationId, string code) => $"{ConversationLinks.Detail(conversationId)}?notice={code}";

    public static string? Describe(string? code) => code switch
    {
        AdvisorChanged => "Asesor actualizado.",
        OutcomeSaved => "Resultado guardado. El clasificador ya no lo cambiará.",
        _ => null,
    };
}
