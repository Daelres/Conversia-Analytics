using Conversia.Application.DTOs.Products;

namespace Conversia.Application.Interfaces;

public interface IProductService
{
    /// <summary>The current tenant's products, one page, ordered by name then SKU. Backs <c>GET /api/products</c>.</summary>
    Task<PagedResponse<ProductResponse>> ListAsync(ProductQuery query, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Exceptions.ProductNotFoundException"/> for a missing or other-tenant product.</summary>
    Task<ProductResponse> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a product in <paramref name="callerTenantId"/>. Throws <see cref="Exceptions.DuplicateSkuException"/>
    /// when the tenant already holds the SKU (case-insensitive, after trimming).
    /// </summary>
    Task<ProductResponse> CreateAsync(Guid callerTenantId, CreateProductRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates name, price and stock. Throws <see cref="Exceptions.ProductNotFoundException"/> or
    /// <see cref="Exceptions.ProductConcurrencyException"/> (stale row version).
    /// </summary>
    Task<ProductResponse> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Exceptions.ProductNotFoundException"/> when there is nothing to delete.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
