using Conversia.Application.DTOs.Products;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

[ApiController]
[Route("api/products")]
[Authorize]
public class ProductsController : ControllerBase
{
    private readonly IProductService _productService;

    public ProductsController(IProductService productService)
    {
        _productService = productService;
    }

    [HttpGet]
    [Authorize(Policy = nameof(Permission.ProductsView))]
    public async Task<ActionResult<PagedResponse<ProductResponse>>> GetProducts(
        [FromQuery] ProductQuery query,
        CancellationToken cancellationToken)
    {
        var products = await _productService.ListAsync(query, cancellationToken);
        return Ok(products);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = nameof(Permission.ProductsView))]
    public async Task<ActionResult<ProductResponse>> GetProduct(Guid id, CancellationToken cancellationToken)
    {
        var product = await _productService.GetAsync(id, cancellationToken);
        return Ok(product);
    }

    [HttpPost]
    [Authorize(Policy = nameof(Permission.ProductsManage))]
    public async Task<ActionResult<ProductResponse>> CreateProduct(
        CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        var product = await _productService.CreateAsync(GetCurrentTenantId(), request, cancellationToken);
        return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, product);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = nameof(Permission.ProductsManage))]
    public async Task<ActionResult<ProductResponse>> UpdateProduct(
        Guid id,
        UpdateProductRequest request,
        CancellationToken cancellationToken)
    {
        var product = await _productService.UpdateAsync(id, request, cancellationToken);
        return Ok(product);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = nameof(Permission.ProductsManage))]
    public async Task<IActionResult> DeleteProduct(Guid id, CancellationToken cancellationToken)
    {
        await _productService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    private Guid GetCurrentTenantId()
        => Guid.Parse(User.FindFirst("tenant_id")!.Value);
}
