using Conversia.Application.DTOs.Admin;
using Conversia.Domain.Enums;

namespace Conversia.Web.Services;

/// <summary>The filters of the administrator's company list as they travel in the query string (<c>estado</c>, <c>buscar</c>, <c>pagina</c>). Anything unknown is ignored.</summary>
public sealed record AdminListFilters(TenantStatus? Status, string? Search, int Page)
{
    public const int PageSize = 20;
    public const int SearchMaxLength = 100;

    private static readonly (string Word, TenantStatus Status)[] Words =
    [
        ("pendiente", TenantStatus.Pending),
        ("activa", TenantStatus.Active),
        ("suspendida", TenantStatus.Suspended),
    ];

    public bool IsFiltered => Status is not null || Search is not null;

    public static AdminListFilters From(string? estado, string? buscar, string? pagina)
    {
        TenantStatus? status = Words.Where(w => string.Equals(w.Word, estado, StringComparison.OrdinalIgnoreCase)).Select(w => (TenantStatus?)w.Status).FirstOrDefault();
        var search = string.IsNullOrWhiteSpace(buscar) ? null : buscar.Trim();
        if (search is { Length: > SearchMaxLength })
        {
            search = search[..SearchMaxLength];
        }

        var page = int.TryParse(pagina, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 1 ? number : 1;
        return new AdminListFilters(status, search, page);
    }

    /// <summary>The word the query string uses for a status.</summary>
    public static string WordFor(TenantStatus status) => Words.First(w => w.Status == status).Word;

    public static IReadOnlyList<TenantStatus> Statuses => Words.Select(w => w.Status).ToList();

    public AdminTenantsQuery ToQuery() => new() { Status = Status?.ToString(), Search = Search, Page = Page, PageSize = PageSize };

    /// <summary>The list at this page with these filters; page 1 is the bare address.</summary>
    public string ToUrl(int page)
    {
        var parts = new List<string>();
        if (Status is { } status)
        {
            parts.Add("estado=" + WordFor(status));
        }

        if (Search is not null)
        {
            parts.Add("buscar=" + Uri.EscapeDataString(Search));
        }

        if (page > 1)
        {
            parts.Add("pagina=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? "/admin" : "/admin?" + string.Join('&', parts);
    }
}

/// <summary>The audit page's filters: an optional company (<c>empresa</c>) and the page (<c>pagina</c>).</summary>
public sealed record AdminAuditFilters(Guid? TenantId, int Page)
{
    public const int PageSize = 20;

    public static AdminAuditFilters From(string? empresa, string? pagina)
    {
        Guid? tenant = Guid.TryParse(empresa, out var id) && id != Guid.Empty ? id : null;
        var page = int.TryParse(pagina, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 1 ? number : 1;
        return new AdminAuditFilters(tenant, page);
    }

    public AdminAuditQuery ToQuery() => new() { TenantId = TenantId, Page = Page, PageSize = PageSize };

    public string ToUrl(int page)
    {
        var parts = new List<string>();
        if (TenantId is { } id)
        {
            parts.Add("empresa=" + id);
        }

        if (page > 1)
        {
            parts.Add("pagina=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? "/admin/auditoria" : "/admin/auditoria?" + string.Join('&', parts);
    }
}
