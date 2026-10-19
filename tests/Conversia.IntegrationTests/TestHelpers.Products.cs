using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Products;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.IntegrationTests;

/// <summary>Helpers shared by the Products integration test classes.</summary>
internal static partial class TestHelpers
{
    public const string ProductsUrl = "/api/products";

    /// <summary>A SKU no other test (and no earlier run) has used, valid under the SKU rules.</summary>
    public static string UniqueSku(string prefix = "SKU") => $"{prefix}-{Guid.NewGuid():N}";

    // ---- Calls -----------------------------------------------------------------------------

    public static Task<HttpResponseMessage> PostProductAsync(
        HttpClient client, string? name, string? sku, decimal price = 10m, int stock = 1)
        => client.PostAsJsonAsync(ProductsUrl, new { name, sku, price, stock });

    /// <summary>Creates a product through the endpoint and asserts it was created (201).</summary>
    public static async Task<ProductResponse> CreateProductAsync(
        HttpClient client, string? name = null, string? sku = null, decimal price = 10.5m, int stock = 3)
    {
        var response = await PostProductAsync(client, name ?? $"Product {Guid.NewGuid():N}", sku ?? UniqueSku(), price, stock);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadProductAsync(response);
    }

    public static async Task<ProductResponse> ReadProductAsync(HttpResponseMessage response)
    {
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>();
        Assert.NotNull(product);
        return product!;
    }

    public static Task<HttpResponseMessage> GetProductAsync(HttpClient client, Guid id)
        => client.GetAsync($"{ProductsUrl}/{id}");

    public static async Task<ProductResponse> GetProductOkAsync(HttpClient client, Guid id)
    {
        var response = await GetProductAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadProductAsync(response);
    }

    public static Task<HttpResponseMessage> PutProductAsync(
        HttpClient client, Guid id, string? name, decimal price, int stock, string? rowVersion)
        => client.PutAsJsonAsync($"{ProductsUrl}/{id}", new { name, price, stock, rowVersion });

    /// <summary>Updates a product with the row version of <paramref name="current"/> and asserts it was accepted (200).</summary>
    public static async Task<ProductResponse> UpdateProductOkAsync(
        HttpClient client, ProductResponse current, string? name = null, decimal? price = null, int? stock = null)
    {
        var response = await PutProductAsync(
            client, current.Id, name ?? current.Name + " v2", price ?? current.Price + 1m, stock ?? current.Stock + 1, current.RowVersion);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadProductAsync(response);
    }

    public static Task<HttpResponseMessage> DeleteProductAsync(HttpClient client, Guid id)
        => client.DeleteAsync($"{ProductsUrl}/{id}");

    /// <param name="query">Query string without the leading '?', e.g. <c>page=2&amp;pageSize=10</c>.</param>
    public static Task<HttpResponseMessage> ListProductsAsync(HttpClient client, string query = "")
        => client.GetAsync(query.Length == 0 ? ProductsUrl : $"{ProductsUrl}?{query}");

    public static async Task<PagedResponse<ProductResponse>> ListProductsOkAsync(HttpClient client, string query = "")
    {
        var response = await ListProductsAsync(client, query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResponse<ProductResponse>>();
        Assert.NotNull(page);
        return page!;
    }

    // ---- Assertions ------------------------------------------------------------------------

    public static void AssertSameProduct(ProductResponse expected, ProductResponse actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Sku, actual.Sku);
        Assert.Equal(expected.Price, actual.Price);
        Assert.Equal(expected.Stock, actual.Stock);
        Assert.Equal(expected.RowVersion, actual.RowVersion);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.CreatedBy, actual.CreatedBy);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.Equal(expected.UpdatedBy, actual.UpdatedBy);
    }

    /// <summary>Asserts the product still reads exactly as <paramref name="expected"/> (nothing was written).</summary>
    public static async Task AssertProductUnchangedAsync(HttpClient ownerClient, ProductResponse expected)
        => AssertSameProduct(expected, await GetProductOkAsync(ownerClient, expected.Id));

    // ---- Seeding and inspection ------------------------------------------------------------

    /// <summary>Persisted products with the SKU (trimmed, case-insensitive) in every tenant, bypassing the tenant filter.</summary>
    public static async Task<List<Product>> FindProductsBySkuAsync(CustomWebApplicationFactory factory, string sku)
    {
        var trimmed = sku.Trim();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.Set<Product>().IgnoreQueryFilters().AsNoTracking().Where(p => p.Sku == trimmed).ToListAsync();
    }

    public static async Task<Product?> FindProductByIdAsync(CustomWebApplicationFactory factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.Set<Product>().IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
    }

    /// <summary>One concurrent call's outcome: the status (null for a transport failure) and the response body.</summary>
    internal sealed record CallResult(HttpStatusCode? Status, string Body)
    {
        public ProductResponse? Product
            => Status is HttpStatusCode.OK or HttpStatusCode.Created
                ? System.Text.Json.JsonSerializer.Deserialize<ProductResponse>(Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                : null;
    }

    /// <summary>Like <see cref="RunInParallelAsync"/> but keeps each response body, in call order.</summary>
    public static async Task<CallResult[]> RunInParallelWithBodiesAsync(IEnumerable<Func<Task<HttpResponseMessage>>> calls)
        => await Task.WhenAll(calls.Select(async call =>
        {
            try
            {
                using var response = await call();
                return new CallResult(response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                return new CallResult(null, ex.ToString());
            }
        }));
}
