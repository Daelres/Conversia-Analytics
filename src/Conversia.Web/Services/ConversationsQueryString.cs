using System.Globalization;
using Conversia.Application.DTOs.Conversations;

namespace Conversia.Web.Services;

/// <summary>The query string of <c>GET /api/conversations</c>: ids and enum names as the API binds them, dates as <c>yyyy-MM-dd</c>.</summary>
internal static class ConversationsQueryString
{
    public static string Build(ConversationQuery query)
    {
        var parts = new List<string>
        {
            $"page={query.Page.ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={query.PageSize.ToString(CultureInfo.InvariantCulture)}",
        };
        Add(parts, "branchId", query.BranchId?.ToString());
        Add(parts, "advisorId", query.AdvisorId?.ToString());
        Add(parts, "unassigned", query.Unassigned is true ? "true" : null);
        Add(parts, "stage", query.Stage);
        Add(parts, "source", query.Source);
        Add(parts, "status", query.Status);
        Add(parts, "from", query.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(parts, "to", query.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(parts, "unanswered", query.Unanswered is true ? "true" : null);
        Add(parts, "closedUnanswered", query.ClosedUnanswered is true ? "true" : null);
        return "?" + string.Join('&', parts);
    }

    private static void Add(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }
}
