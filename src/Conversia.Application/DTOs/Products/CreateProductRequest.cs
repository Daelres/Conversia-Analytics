using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;

namespace Conversia.Application.DTOs.Products;

/// <summary>
/// Body of <c>POST /api/products</c>. Carries no tenant: the product always lands in the caller's tenant.
/// </summary>
public class CreateProductRequest
{
    [Display(Name = "Nombre")]
    [ProductName]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "SKU")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [ProductSku]
    public string Sku { get; set; } = string.Empty;

    [Display(Name = "Precio")]
    [ProductPrice]
    public decimal Price { get; set; }

    [Display(Name = "Stock")]
    [Range(0, int.MaxValue, ErrorMessage = ValidationMessages.Range)]
    public int Stock { get; set; }
}
