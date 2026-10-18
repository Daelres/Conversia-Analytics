using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Products;
using Conversia.Application.Validation;

namespace Conversia.Web.Services;

/// <summary>
/// A product form as posted. Price and stock stay text: static SSR binding would turn an unparsable, blank or missing
/// number into 0, which would then be saved as something the user never typed.
/// </summary>
public class ProductInput
{
    [Display(Name = "Nombre")]
    [ProductName]
    public string? Name { get; set; }

    [Display(Name = "Precio")]
    [PriceText]
    public string? Price { get; set; }

    [Display(Name = "Stock")]
    [StockText]
    public string? Stock { get; set; }

    /// <summary>Only meaningful once the input passed validation.</summary>
    protected decimal ParsedPrice => ProductNumbers.TryParsePrice(Price, out var price) ? price : throw NotValidated();

    protected int ParsedStock => ProductNumbers.TryParseStock(Stock, out var stock) ? stock : throw NotValidated();

    private static InvalidOperationException NotValidated() => new("The product input was read before it was validated.");
}

public sealed class NewProductInput : ProductInput
{
    [Display(Name = "SKU")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [ProductSku]
    public string? Sku { get; set; }

    public CreateProductRequest ToRequest()
        => new() { Sku = Sku!, Name = Name!, Price = ParsedPrice, Stock = ParsedStock };
}

public sealed class EditProductInput : ProductInput
{
    [Display(Name = "Versión")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [Base64]
    public string? RowVersion { get; set; }

    public UpdateProductRequest ToRequest()
        => new() { Name = Name!, Price = ParsedPrice, Stock = ParsedStock, RowVersion = RowVersion! };
}
