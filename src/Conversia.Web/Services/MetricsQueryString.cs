using System.Globalization;
using Conversia.Application.DTOs.Metrics;

namespace Conversia.Web.Services;

/// <summary>The query string of the metrics endpoints (docs/METRICS.md): local dates as <c>yyyy-MM-dd</c>, ids and names as the API binds them.</summary>
internal static class MetricsQueryString
{
    public static string Build(MetricsQuery query)
    {
        var parts = new List<string>();
        Add(parts, "from", query.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(parts, "to", query.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(parts, "branchId", query.BranchId?.ToString());
        Add(parts, "advisorId", query.AdvisorId?.ToString());
        Add(parts, "unassigned", query.Unassigned is true ? "true" : null);
        Add(parts, "source", query.Source);
        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    private static void Add(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }
}
