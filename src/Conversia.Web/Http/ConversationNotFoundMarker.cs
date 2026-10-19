namespace Conversia.Web.Http;

/// <summary>
/// The status-code pages re-run every 404 into the generic "Not found" page. A conversation page that answers 404 marks the request
/// so that page can say which thing is missing (same technique as <see cref="ProductNotFoundMarker"/>).
/// </summary>
public static class ConversationNotFoundMarker
{
    private const string Key = "Conversia.ConversationNotFound";

    public static void Mark(HttpContext context) => context.Items[Key] = true;

    public static bool IsMarked(HttpContext? context) => context?.Items.ContainsKey(Key) == true;
}
