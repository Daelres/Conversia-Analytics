namespace Conversia.Web.Http;

/// <summary>
/// The status-code pages re-run every 404 into the generic "Not found" page. A product page that answers 404 marks the
/// request so that this page can say which thing is missing; the marker lives as long as the request, re-execution included.
/// </summary>
public static class ProductNotFoundMarker
{
    private const string Key = "Conversia.ProductNotFound";

    public static void Mark(HttpContext context) => context.Items[Key] = true;

    public static bool IsMarked(HttpContext? context) => context?.Items.ContainsKey(Key) == true;
}
