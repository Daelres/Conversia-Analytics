namespace Conversia.Web.Http;

/// <summary>
/// The status-code pages re-run every 404 into the generic "Not found" page. A page that answers 404 on purpose marks the request with
/// what is missing, so that page can say it; the marker lives as long as the request, re-execution included.
/// </summary>
public static class NotFoundMarker
{
    public const string Conversation = "conversation";
    public const string Channel = "channel";
    public const string Company = "company";

    private const string Key = "Conversia.NotFoundKind";

    public static void Mark(HttpContext context, string kind) => context.Items[Key] = kind;

    public static bool IsMarked(HttpContext? context, string kind) => context?.Items.TryGetValue(Key, out var value) == true && Equals(value, kind);
}
