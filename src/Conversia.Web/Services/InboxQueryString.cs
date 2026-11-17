using System.Globalization;
using Conversia.Application.DTOs.Inbox;

namespace Conversia.Web.Services;

/// <summary>The query string of <c>GET /api/inbox</c>, and the inbox filters as the <c>/bandeja</c> page reads them from its own address.</summary>
public static class InboxQueryString
{
    public static string Build(InboxQuery query)
    {
        var parts = new List<string>();
        if (query.Mine == true)
        {
            parts.Add("mine=true");
        }

        if (query.BranchId is { } branch)
        {
            parts.Add($"branchId={branch}");
        }

        if (query.Unassigned == true)
        {
            parts.Add("unassigned=true");
        }

        if (query.AwaitingReply == true)
        {
            parts.Add("awaitingReply=true");
        }

        if (query.Since is { } since)
        {
            parts.Add($"since={Uri.EscapeDataString(since.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    /// <summary>
    /// The inbox filters of the page's address (<c>filtro=mias|sin-asesor|esperando</c>, <c>sucursal={id}</c>). Anything unknown is ignored:
    /// the address is the person's, and a bad filter shows the whole inbox rather than an error.
    /// </summary>
    public static InboxQuery FromPage(string? filter, string? branch) => new()
    {
        Mine = filter == Mine ? true : null,
        Unassigned = filter == Unassigned ? true : null,
        AwaitingReply = filter == Awaiting ? true : null,
        BranchId = Guid.TryParse(branch, out var id) ? id : null,
    };

    public const string Mine = "mias";
    public const string Unassigned = "sin-asesor";
    public const string Awaiting = "esperando";

    /// <summary>The page's own query string for the same filters (the list fragment is asked for with it).</summary>
    public static string PageQuery(string? filter, Guid? branch)
    {
        var parts = new List<string>();
        if (filter is Mine or Unassigned or Awaiting)
        {
            parts.Add($"filtro={filter}");
        }

        if (branch is { } id)
        {
            parts.Add($"sucursal={id}");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }
}
