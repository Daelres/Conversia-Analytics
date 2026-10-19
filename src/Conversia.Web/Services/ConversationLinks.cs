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
        => List(new ConversationFilters
        {
            BranchId = branchId,
            AdvisorId = advisorId,
            Stage = stage,
            Source = source,
            Status = status,
            From = range?.From,
            To = range?.To,
            UnansweredOpen = unanswered,
        });

    /// <summary>The list with these filters; the first page carries no <c>page</c>.</summary>
    public static string List(ConversationFilters filters)
    {
        var parts = new List<string>();
        Add(parts, "branch", filters.BranchId?.ToString());
        Add(parts, "advisor", filters.Unassigned ? ConversationFilters.NoAdvisor : filters.AdvisorId?.ToString());
        Add(parts, "stage", filters.Stage);
        Add(parts, "source", filters.Source);
        Add(parts, "status", filters.Status);
        Add(parts, "reply", filters.UnansweredOpen ? ConversationFilters.ReplyOpen : filters.ClosedUnanswered ? ConversationFilters.ReplyClosed : null);
        Add(parts, "from", filters.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(parts, "to", filters.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (filters.Page > 1)
        {
            Add(parts, "page", filters.Page.ToString(CultureInfo.InvariantCulture));
        }

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
