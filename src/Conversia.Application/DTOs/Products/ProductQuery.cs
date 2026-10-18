using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;

namespace Conversia.Application.DTOs.Products;

/// <summary>Query string of <c>GET /api/products</c>.</summary>
public class ProductQuery
{
    public const int MaxPageSize = 100;
    public const int MaxSearchLength = 100;

    [Display(Name = "Página")]
    [Range(1, int.MaxValue, ErrorMessage = ValidationMessages.Range)]
    public int Page { get; set; } = 1;

    [Display(Name = "Tamaño de página")]
    [Range(1, MaxPageSize, ErrorMessage = ValidationMessages.Range)]
    public int PageSize { get; set; } = 20;

    [Display(Name = "Búsqueda")]
    [MaxLength(MaxSearchLength, ErrorMessage = ValidationMessages.MaxLength)]
    public string? Search { get; set; }
}
