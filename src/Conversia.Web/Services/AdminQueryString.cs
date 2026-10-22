using System.Globalization;
using Conversia.Application.DTOs.Admin;

namespace Conversia.Web.Services;

/// <summary>The query strings of the administrator's two listings. Values are escaped; an empty filter is left out.</summary>
public static class AdminQueryString
{
    public static string Build(AdminTenantsQuery query)
        => Join(
            ("status", query.Status),
            ("search", query.Search),
            ("page", query.Page.ToString(CultureInfo.InvariantCulture)),
            ("pageSize", query.PageSize.ToString(CultureInfo.InvariantCulture)));

    public static string Build(AdminAuditQuery query)
        => Join(
            ("tenantId", query.TenantId?.ToString()),
            ("page", query.Page.ToString(CultureInfo.InvariantCulture)),
            ("pageSize", query.PageSize.ToString(CultureInfo.InvariantCulture)));

    private static string Join(params (string Name, string? Value)[] pairs)
    {
        var parts = pairs
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}")
            .ToList();
        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }
}
