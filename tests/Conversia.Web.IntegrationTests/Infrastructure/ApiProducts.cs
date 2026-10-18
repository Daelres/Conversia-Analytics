using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Products;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>Arranges and inspects products by talking to the API directly (as a tenant's owner or member), bypassing the Web under test.</summary>
internal static class ApiProducts
{
    /// <summary>A SKU no other test has used, valid under the SKU rules.</summary>
    public static string UniqueSku(string prefix = "P") => $"{prefix}-{Guid.NewGuid():N}";

    public static string UniqueName(string prefix = "Widget") => $"{prefix} {Guid.NewGuid():N}";

    public static async Task<string> LoginTokenAsync(TestApiFactory api, string email, string password = ApiSeed.StrongPassword)
    {
        using var client = api.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Conversia.Application.DTOs.Auth.AuthResponse>())!.Token;
    }

    public static async Task<ProductResponse> CreateAsync(
        TestApiFactory api, string token, string? name = null, string? sku = null, decimal price = 10.5m, int stock = 3)
    {
        using var client = ApiSeed.AuthorizedClient(api, token);
        using var response = await client.PostAsJsonAsync("/api/products", new { name = name ?? UniqueName(), sku = sku ?? UniqueSku(), price, stock });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    public static async Task<(HttpStatusCode Status, ProductResponse? Product)> TryGetAsync(TestApiFactory api, string token, Guid id)
    {
        using var client = ApiSeed.AuthorizedClient(api, token);
        using var response = await client.GetAsync($"/api/products/{id}");
        return (response.StatusCode, response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<ProductResponse>() : null);
    }

    public static async Task<ProductResponse> GetAsync(TestApiFactory api, string token, Guid id)
    {
        var (status, product) = await TryGetAsync(api, token, id);
        Assert.Equal(HttpStatusCode.OK, status);
        return product!;
    }

    /// <summary>Every product of the caller's tenant (the API's largest page), optionally filtered.</summary>
    public static async Task<IReadOnlyList<ProductResponse>> ListAsync(TestApiFactory api, string token, string? search = null)
    {
        using var client = ApiSeed.AuthorizedClient(api, token);
        var url = "/api/products?pageSize=100" + (search is null ? string.Empty : "&search=" + Uri.EscapeDataString(search));
        var page = await client.GetFromJsonAsync<PagedResponse<ProductResponse>>(url);
        return page!.Items;
    }

    public static async Task<IReadOnlyList<ProductResponse>> FindBySkuAsync(TestApiFactory api, string token, string sku)
        => (await ListAsync(api, token, sku)).Where(p => string.Equals(p.Sku, sku, StringComparison.OrdinalIgnoreCase)).ToList();

    public static async Task<ProductResponse> UpdateAsync(
        TestApiFactory api, string token, ProductResponse current, string name, decimal price, int stock)
    {
        using var client = ApiSeed.AuthorizedClient(api, token);
        using var response = await client.PutAsJsonAsync($"/api/products/{current.Id}", new { name, price, stock, rowVersion = current.RowVersion });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    public static async Task DeleteAsync(TestApiFactory api, string token, Guid id)
    {
        using var client = ApiSeed.AuthorizedClient(api, token);
        using var response = await client.DeleteAsync($"/api/products/{id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>What the API itself answers (400) for a product body: the reference for "the Web shows the API's messages".</summary>
    public static async Task<string[]> ValidationMessagesAsync(TestApiFactory api, string token, HttpMethod method, string path, object body)
    {
        using var client = ApiSeed.AuthorizedClient(api, token);
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>();
        return problem!.Errors.Values.SelectMany(m => m).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToArray();
    }
}

/// <summary>A freshly registered tenant that already owns a few products.</summary>
internal sealed record Shop(ApiSeed.Owner Owner, IReadOnlyList<ProductResponse> Products)
{
    public ProductResponse Primary => Products[0];

    public static async Task<Shop> CreateAsync(TestApiFactory api, int products = 3, string prefix = "shop")
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, prefix);
        var created = new List<ProductResponse>();
        for (var i = 0; i < products; i++)
        {
            created.Add(await ApiProducts.CreateAsync(api, owner.Token, ApiProducts.UniqueName($"Item{i}"), ApiProducts.UniqueSku($"S{i}"), 10m + i + 0.25m, 5 + i));
        }

        return new Shop(owner, created);
    }
}
