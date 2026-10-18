using Conversia.Domain.Entities;

namespace Conversia.Domain.Interfaces;

public interface IProductRepository
{
    /// <summary>
    /// Untracked, read-only lookup. Tenant-scoped: another tenant's product is indistinguishable from a missing one (null).
    /// </summary>
    Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tracked lookup for the load-modify-<see cref="UpdateAsync"/> path; same tenant scoping as <see cref="GetByIdAsync"/>.
    /// </summary>
    Task<Product?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the current tenant's products ordered by name then SKU, plus the total number of
    /// products matching <paramref name="search"/> (a literal, case-insensitive "contains" on name or SKU; null = no filter).
    /// </summary>
    Task<(IReadOnlyList<Product> Items, int TotalCount)> ListAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Persists a new product. Throws DuplicateSkuException when the tenant already holds the SKU.</summary>
    Task AddAsync(Product product, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the changes made to a product loaded through <see cref="GetForUpdateAsync"/>, guarded by the row version
    /// the client last read. Throws ProductConcurrencyException when the row changed since, and
    /// ProductNotFoundException when it no longer exists.
    /// </summary>
    Task UpdateAsync(Product product, uint expectedRowVersion, CancellationToken cancellationToken = default);

    /// <summary>Deletes the product; false when it does not exist in the current tenant.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
