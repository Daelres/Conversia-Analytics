using Conversia.Application.Exceptions;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Repositories;

// Every query is scoped to the caller's tenant by the global query filter on Products;
// no method here repeats a TenantId predicate.
public class ProductRepository : IProductRepository
{
    private const string LikeEscape = "\\";

    private readonly ApplicationDbContext _dbContext;

    public ProductRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _dbContext.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Product?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken = default)
        => _dbContext.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<Product> Items, int TotalCount)> ListAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var products = _dbContext.Products.AsNoTracking();

        if (search is not null)
        {
            // PostgreSQL text cannot hold a NUL character (it rejects the parameter), and so no stored name or SKU contains one.
            if (search.Contains('\0'))
            {
                return (Array.Empty<Product>(), 0);
            }

            // ILIKE gives the case-insensitive "contains" (PostgreSQL comparisons are case-sensitive by default);
            // the wildcards of the search text are escaped first so it stays a literal substring.
            var pattern = $"%{EscapeLikePattern(search)}%";
            products = products.Where(p => EF.Functions.ILike(p.Name, pattern, LikeEscape) || EF.Functions.ILike(p.Sku, pattern, LikeEscape));
        }

        var totalCount = await products.CountAsync(cancellationToken);

        // long: page * pageSize can exceed int.MaxValue. A page past the end needs no second query.
        var offset = (long)(page - 1) * pageSize;
        if (offset >= totalCount)
        {
            return (Array.Empty<Product>(), totalCount);
        }

        var items = await products
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Sku)
            .Skip((int)offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    private static string EscapeLikePattern(string text)
        => text.Replace(LikeEscape, LikeEscape + LikeEscape).Replace("%", LikeEscape + "%").Replace("_", LikeEscape + "_");

    public async Task AddAsync(Product product, CancellationToken cancellationToken = default)
    {
        await _dbContext.Products.AddAsync(product, cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // The unique (TenantId, Sku) index is the arbiter of concurrent creates, the same
            // pattern UserRepository uses for emails.
            throw new DuplicateSkuException();
        }
    }

    public async Task UpdateAsync(Product product, uint expectedRowVersion, CancellationToken cancellationToken = default)
    {
        // Turns an already-stale token away without the UPDATE. A change landing after the load is
        // still caught on save: EF filters the UPDATE on the loaded (original) RowVersion.
        if (product.RowVersion != expectedRowVersion)
        {
            throw new ProductConcurrencyException();
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var stillExists = await ex.Entries.Single().GetDatabaseValuesAsync(cancellationToken) is not null;
            throw stillExists ? new ProductConcurrencyException() : new ProductNotFoundException();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => await _dbContext.Products.Where(p => p.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
}
