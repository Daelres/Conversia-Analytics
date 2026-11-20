using System.Globalization;
using Conversia.Application.DTOs.Admin;
using Conversia.Domain.Enums;

namespace Conversia.Web.Services;

/// <summary>The filters of the licence inventory as the address carries them (Spanish words), and the query they become.</summary>
public sealed record LicenseListFilters(LineLicenseStatus? Status, int Page)
{
    public const int PageSize = 20;

    public static readonly IReadOnlyList<(LineLicenseStatus Status, string Word, string Label)> Statuses =
    [
        (LineLicenseStatus.Available, "disponible", "Disponibles"),
        (LineLicenseStatus.Assigned, "asignada", "Asignadas"),
        (LineLicenseStatus.Retired, "retirada", "Retiradas"),
    ];

    public static LicenseListFilters From(string? estado, string? pagina)
    {
        var status = Statuses.FirstOrDefault(s => string.Equals(s.Word, estado?.Trim(), StringComparison.OrdinalIgnoreCase));
        var page = int.TryParse(pagina, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p >= 1 ? p : 1;
        return new LicenseListFilters(status.Word is null ? null : status.Status, page);
    }

    public string? Word => Statuses.FirstOrDefault(s => s.Status == Status).Word;

    public AdminLicensesQuery ToQuery() => new() { Status = Status?.ToString(), Page = Page, PageSize = PageSize };

    public string ToUrl(int page)
    {
        var parts = new List<string>();
        if (Word is { } word)
        {
            parts.Add($"estado={word}");
        }

        if (page > 1)
        {
            parts.Add($"pagina={page.ToString(CultureInfo.InvariantCulture)}");
        }

        return parts.Count == 0 ? "/admin/licencias" : "/admin/licencias?" + string.Join('&', parts);
    }
}
