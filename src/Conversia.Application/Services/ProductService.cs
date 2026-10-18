using Conversia.Application.DTOs.Products;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

public class ProductService : IProductService
{
    private readonly IProductRepository _productRepository;

    public ProductService(IProductRepository productRepository)
    {
        _productRepository = productRepository;
    }

    public async Task<PagedResponse<ProductResponse>> ListAsync(ProductQuery query, CancellationToken cancellationToken = default)
    {
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var (items, totalCount) = await _productRepository.ListAsync(search, query.Page, query.PageSize, cancellationToken);

        return new PagedResponse<ProductResponse>
        {
            Items = items.Select(ProductResponse.From).ToList(),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = totalCount,
        };
    }

    public async Task<ProductResponse> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await _productRepository.GetByIdAsync(id, cancellationToken)
            ?? throw new ProductNotFoundException();
        return ProductResponse.From(product);
    }

    public async Task<ProductResponse> CreateAsync(Guid callerTenantId, CreateProductRequest request, CancellationToken cancellationToken = default)
    {
        var product = Product.Create(callerTenantId, request.Name, request.Sku, request.Price, request.Stock);

        // No existence pre-check: it would cost a round trip and still be racy, so the unique
        // index decides and AddAsync reports the loser as DuplicateSkuException.
        await _productRepository.AddAsync(product, cancellationToken);

        return ProductResponse.From(product);
    }

    public async Task<ProductResponse> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default)
    {
        if (!StrictBase64.TryDecode(request.RowVersion, out var expectedRowVersion))
        {
            throw new DomainException("La versión de la fila debe ser el valor en base64 devuelto por la última lectura.");
        }

        var product = await _productRepository.GetForUpdateAsync(id, cancellationToken)
            ?? throw new ProductNotFoundException();

        product.Update(request.Name, request.Price, request.Stock);
        await _productRepository.UpdateAsync(product, RowVersionToken.ToRowVersion(expectedRowVersion), cancellationToken);

        return ProductResponse.From(product);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await _productRepository.DeleteAsync(id, cancellationToken))
        {
            throw new ProductNotFoundException();
        }
    }
}
