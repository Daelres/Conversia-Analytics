using Conversia.Domain.Entities;

namespace Conversia.Application.DTOs.Products;

public class ProductResponse
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Sku { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public int Stock { get; set; }

    /// <summary>Base64 of the concurrency token; send it back unchanged on update.</summary>
    public string RowVersion { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public static ProductResponse From(Product product) => new()
    {
        Id = product.Id,
        Name = product.Name,
        Sku = product.Sku,
        Price = product.Price,
        Stock = product.Stock,
        RowVersion = RowVersionToken.Encode(product.RowVersion),
        CreatedAt = product.CreatedAt,
        CreatedBy = product.CreatedBy,
        UpdatedAt = product.UpdatedAt,
        UpdatedBy = product.UpdatedBy,
    };
}
