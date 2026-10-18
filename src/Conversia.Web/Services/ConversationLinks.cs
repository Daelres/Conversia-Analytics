using System.Globalization;
using Conversia.Web.Dashboard;

namespace Conversia.Web.Services;

/// <summary>The addresses of the conversation pages, in one place so the dashboard's links and the list's filters agree on the query-string names.</summary>
public static class ConversationLinks
{
    public const string ListPath = "/conversaciones";

    public static string Detail(Guid id) => $"{ListPath}/{id}";

    /// <summary>The list, filtered. <paramref name="range"/> is in local dates, <c>To</c> exclusive, as the API takes them.</summary>
    public static string List(
        DateRange? range = null, Guid? branchId = null, Guid? advisorId = null, string? stage = null, string? source = null, string? status = null, bool unanswered = false)
    {
        var parts = new List<string>();
        Add(parts, "branch", branchId?.ToString());
        Add(parts, "advisor", advisorId?.ToString());
        Add(parts, "stage", stage);
        Add(parts, "source", source);
        Add(parts, "status", status);
        Add(parts, "unanswered", unanswered ? "true" : null);
        Add(parts, "from", range?.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(parts, "to", range?.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return parts.Count == 0 ? ListPath : $"{ListPath}?{string.Join('&', parts)}";
    }

    private static void Add(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }
}
