using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;

namespace Conversia.Application.DTOs.Products;

/// <summary>
/// Body of <c>PUT /api/products/{id}</c>. The SKU cannot change, so it is not part of the contract.
/// <see cref="RowVersion"/> is the base64 value returned by the last read of the product.
/// </summary>
public class UpdateProductRequest
{
    [Display(Name = "Nombre")]
    [ProductName]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Precio")]
    [ProductPrice]
    public decimal Price { get; set; }

    [Display(Name = "Stock")]
    [Range(0, int.MaxValue, ErrorMessage = ValidationMessages.Range)]
    public int Stock { get; set; }

    [Display(Name = "Versión")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [Base64]
    public string RowVersion { get; set; } = string.Empty;
}
