using System.Net;
using System.Text.Json;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Products;
using Conversia.Web.Services;

namespace Conversia.Web.IntegrationTests.Services;

/// <summary>
/// The product calls of the Web's API client: verb, URL and query (the search term URL-encoded), the bearer token per call,
/// the base64 <c>rowVersion</c> sent back untouched, and the same status mapping as every other call. Same stub-handler
/// approach as the rest of <see cref="ApiClientTests"/>; no host, no database.
/// </summary>
public partial class ApiClientTests
{
    private static readonly Guid ProductId = Guid.Parse("c0ffee00-1111-4222-8333-444455556666");

    private static object ProductJson(string name = "Anvil", string sku = "ACME-001", decimal price = 249.90m, int stock = 14, string rowVersion = "AAAAAAAAB9E=")
        => new { id = ProductId, name, sku, price, stock, rowVersion, createdAt = DateTime.UtcNow, createdBy = UserId, updatedAt = (DateTime?)null, updatedBy = (Guid?)null };

    private static object PageJson(params object[] items)
        => new { items, page = 2, pageSize = 10, totalCount = 13 };

    // ---- list ----

    [Fact]
    public async Task GetProducts_sends_a_get_with_page_pageSize_search_and_the_bearer_token_and_maps_the_page()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, PageJson(ProductJson(), ProductJson("Rocket", "ACME-002", 1299m, 3))));

        var result = await Client(stub).GetProductsAsync("my-token", page: 2, pageSize: 10, search: "anvil");

        Assert.Equal(ApiResultStatus.Success, result.Status);
        Assert.Equal(2, result.Value!.Page);
        Assert.Equal(10, result.Value.PageSize);
        Assert.Equal(13, result.Value.TotalCount);
        Assert.Collection(result.Value.Items,
            first =>
            {
                Assert.Equal(ProductId, first.Id);
                Assert.Equal("Anvil", first.Name);
                Assert.Equal("ACME-001", first.Sku);
                Assert.Equal(249.90m, first.Price);
                Assert.Equal(14, first.Stock);
                Assert.Equal("AAAAAAAAB9E=", first.RowVersion);
            },
            second => Assert.Equal("ACME-002", second.Sku));
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("/api/products", call.Uri.AbsolutePath);
        Assert.Equal("Bearer my-token", call.Authorization);
        Assert.Null(call.Body);
        var query = System.Web.HttpUtility.ParseQueryString(call.Uri.Query);
        Assert.Equal("2", query["page"]);
        Assert.Equal("10", query["pageSize"]);
        Assert.Equal("anvil", query["search"]);
    }

    public static TheoryData<string> AwkwardSearchTerms => new()
    {
        "two words",
        "a&b=c",
        "100%",
        "50%25",
        "x+y",
        "tab\there",
        "#fragment",
        "a?b",
        "a/b\\c",
        "\"><script>alert(1)</script>",
        "<img src=x onerror=alert(1)>",
        "Ünïcödé 日本語 🚀",
        "semi;colon,comma",
        "page=9&pageSize=1000",
    };

    [Theory]
    [MemberData(nameof(AwkwardSearchTerms))]
    public async Task GetProducts_url_encodes_the_search_term_so_it_arrives_intact_and_cannot_add_parameters(string term)
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, PageJson()));

        var result = await Client(stub).GetProductsAsync("t", 1, 10, term);

        Assert.Equal(ApiResultStatus.Success, result.Status);
        var call = Assert.Single(stub.Calls);
        Assert.Equal("/api/products", call.Uri.AbsolutePath);
        Assert.Equal(string.Empty, call.Uri.Fragment);
        var query = System.Web.HttpUtility.ParseQueryString(call.Uri.Query);
        Assert.Equal(new[] { "page", "pageSize", "search" }, query.AllKeys.Order().ToArray());
        Assert.Equal(term, query["search"]);
        Assert.Equal("1", query["page"]);
        Assert.Equal("10", query["pageSize"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetProducts_with_a_blank_search_applies_no_filter(string? blank)
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, PageJson()));

        var result = await Client(stub).GetProductsAsync("t", 1, 10, blank);

        Assert.Equal(ApiResultStatus.Success, result.Status);
        var query = System.Web.HttpUtility.ParseQueryString(Assert.Single(stub.Calls).Uri.Query);
        Assert.True(string.IsNullOrWhiteSpace(query["search"]), $"A blank search must not filter, but the query was '{query}'.");
        Assert.Equal("1", query["page"]);
    }

    // ---- single product ----

    [Fact]
    public async Task GetProduct_requests_the_product_by_id_with_the_bearer_token()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, ProductJson()));

        var result = await Client(stub).GetProductAsync("my-token", ProductId);

        Assert.Equal(ApiResultStatus.Success, result.Status);
        Assert.Equal(ProductId, result.Value!.Id);
        Assert.Equal("AAAAAAAAB9E=", result.Value.RowVersion);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal($"/api/products/{ProductId}", call.Uri.AbsolutePath);
        Assert.Equal("Bearer my-token", call.Authorization);
    }

    // ---- writes ----

    [Fact]
    public async Task CreateProduct_201_posts_the_four_fields_as_json_and_maps_the_created_product()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.Created, ProductJson("Boots", "ACME-012", 119m, 15)));

        var result = await Client(stub).CreateProductAsync("my-token", new CreateProductRequest { Name = "Boots", Sku = "ACME-012", Price = 119m, Stock = 15 });

        Assert.Equal(ApiResultStatus.Success, result.Status);
        Assert.Equal("ACME-012", result.Value!.Sku);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/api/products", call.Uri.AbsolutePath);
        Assert.Equal(string.Empty, call.Uri.Query);
        Assert.Equal("Bearer my-token", call.Authorization);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal(new[] { "name", "price", "sku", "stock" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("Boots", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("ACME-012", body.RootElement.GetProperty("sku").GetString());
        Assert.Equal(119m, body.RootElement.GetProperty("price").GetDecimal());
        Assert.Equal(15, body.RootElement.GetProperty("stock").GetInt32());
    }

    [Theory]
    [InlineData("AAAAAAAAB9E=")]
    [InlineData("+/+/AAA=")]
    [InlineData("///+++==")]
    public async Task UpdateProduct_200_puts_the_fields_and_sends_the_row_version_back_unchanged(string rowVersion)
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, ProductJson("Anvil v2", "ACME-001", 260m, 9, "AAAAAAAAB9F=")));

        var result = await Client(stub).UpdateProductAsync("my-token", ProductId,
            new UpdateProductRequest { Name = "Anvil v2", Price = 260m, Stock = 9, RowVersion = rowVersion });

        Assert.Equal(ApiResultStatus.Success, result.Status);
        Assert.Equal("Anvil v2", result.Value!.Name);
        Assert.Equal("AAAAAAAAB9F=", result.Value.RowVersion);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal($"/api/products/{ProductId}", call.Uri.AbsolutePath);
        Assert.Equal(string.Empty, call.Uri.Query);
        Assert.Equal("Bearer my-token", call.Authorization);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal(new[] { "name", "price", "rowVersion", "stock" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(rowVersion, body.RootElement.GetProperty("rowVersion").GetString());
        Assert.Equal("Anvil v2", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(260m, body.RootElement.GetProperty("price").GetDecimal());
        Assert.Equal(9, body.RootElement.GetProperty("stock").GetInt32());
    }

    [Fact]
    public async Task DeleteProduct_204_deletes_by_id_with_the_bearer_token()
    {
        var stub = new StubHandler(_ => Respond(HttpStatusCode.NoContent));

        var result = await Client(stub).DeleteProductAsync("my-token", ProductId);

        Assert.Equal(ApiResultStatus.Success, result.Status);
        var call = Assert.Single(stub.Calls);
        Assert.Equal(HttpMethod.Delete, call.Method);
        Assert.Equal($"/api/products/{ProductId}", call.Uri.AbsolutePath);
        Assert.Equal("Bearer my-token", call.Authorization);
        Assert.Null(call.Body);
    }

    // ---- status mapping ----

    private static async Task<Dictionary<string, ApiResult<object?>>> AllProductCallsAsync(ApiClient client)
    {
        static ApiResult<object?> Erase<T>(ApiResult<T> result) => new()
        {
            Status = result.Status,
            Value = result.Value,
            ValidationErrors = result.ValidationErrors,
            RetryAfterSeconds = result.RetryAfterSeconds,
        };

        return new Dictionary<string, ApiResult<object?>>
        {
            ["GetProducts"] = Erase(await client.GetProductsAsync("t", 1, 10, "x")),
            ["GetProduct"] = Erase(await client.GetProductAsync("t", ProductId)),
            ["CreateProduct"] = Erase(await client.CreateProductAsync("t", new CreateProductRequest { Name = "n", Sku = "s", Price = 1m, Stock = 1 })),
            ["UpdateProduct"] = Erase(await client.UpdateProductAsync("t", ProductId, new UpdateProductRequest { Name = "n", Price = 1m, Stock = 1, RowVersion = "AAAAAAAAB9E=" })),
            ["DeleteProduct"] = Erase(await client.DeleteProductAsync("t", ProductId)),
        };
    }

    [Theory]
    [MemberData(nameof(StatusMapping))]
    public async Task Product_calls_map_error_statuses(int status, ApiResultStatus expected)
    {
        var stub = new StubHandler(_ => Respond((HttpStatusCode)status, """{"title":"x","status":0}"""));

        var results = await AllProductCallsAsync(Client(stub));

        Assert.Equal(5, stub.Calls.Count);
        foreach (var (call, result) in results)
        {
            AssertStatus(status, call, expected, result.Status);
            Assert.False(result.IsSuccess);
        }
    }

    [Fact]
    public async Task Product_calls_map_a_400_problem_to_the_per_field_messages()
    {
        var stub = new StubHandler(_ => Respond(HttpStatusCode.BadRequest, """
            {"title":"One or more validation errors occurred.","status":400,
             "errors":{"Name":["Name is required."],"Price":["Price must be 0 or more.","At most two decimals."]}}
            """));

        var results = await AllProductCallsAsync(Client(stub));

        foreach (var (call, result) in results)
        {
            AssertStatus(400, call, ApiResultStatus.ValidationFailed, result.Status);
            Assert.Equal(new[] { "Name is required." }, result.ValidationErrors["Name"]);
            Assert.Equal(new[] { "Price must be 0 or more.", "At most two decimals." }, result.ValidationErrors["Price"]);
        }
    }

    [Fact]
    public async Task Product_calls_map_a_429_to_rate_limited_with_the_retry_after_seconds()
    {
        var stub = new StubHandler(_ => Respond(HttpStatusCode.TooManyRequests, """{"title":"Too Many Requests","status":429}""", retryAfter: "17"));

        var results = await AllProductCallsAsync(Client(stub));

        foreach (var (call, result) in results)
        {
            AssertStatus(429, call, ApiResultStatus.RateLimited, result.Status);
            Assert.True(result.RetryAfterSeconds == 17, $"{call}: expected Retry-After 17 but got {result.RetryAfterSeconds}.");
        }
    }

    [Theory]
    [MemberData(nameof(BrokenBackends))]
    public async Task Product_calls_degrade_to_Unavailable_when_the_backend_misbehaves(string failure)
    {
        var client = Client(BrokenStub(failure));

        AssertStatus(failure, "GetProducts", ApiResultStatus.Unavailable, (await client.GetProductsAsync("t", 1, 10, null)).Status);
        AssertStatus(failure, "GetProduct", ApiResultStatus.Unavailable, (await client.GetProductAsync("t", ProductId)).Status);
        AssertStatus(failure, "CreateProduct", ApiResultStatus.Unavailable, (await client.CreateProductAsync("t", new CreateProductRequest { Name = "n", Sku = "s", Price = 1m, Stock = 1 })).Status);
        AssertStatus(failure, "UpdateProduct", ApiResultStatus.Unavailable, (await client.UpdateProductAsync("t", ProductId, new UpdateProductRequest { Name = "n", Price = 1m, Stock = 1, RowVersion = "AAAAAAAAB9E=" })).Status);
    }

    [Theory]
    [InlineData("connection refused")]
    [InlineData("timeout")]
    [InlineData("html 400")]
    public async Task DeleteProduct_degrades_to_Unavailable_when_the_backend_cannot_be_reached_or_answers_garbage(string failure)
    {
        var result = await Client(BrokenStub(failure)).DeleteProductAsync("t", ProductId);

        AssertStatus(failure, "DeleteProduct", ApiResultStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task GetProducts_with_a_body_that_is_not_a_page_degrades_to_Unavailable()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, new[] { ProductJson() }));

        var result = await Client(stub).GetProductsAsync("t", 1, 10, null);

        Assert.Equal(ApiResultStatus.Unavailable, result.Status);
        Assert.Null(result.Value);
    }

    // ---- token per call, cancellation ----

    [Fact]
    public async Task Concurrent_product_calls_with_different_tokens_never_mix_them_up()
    {
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        var stub = new StubHandler(async request =>
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                bothArrived.SetResult();
            }

            await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var bearer = request.Headers.Authorization!.Parameter!;
            return await Json(HttpStatusCode.OK, PageJson(ProductJson(name: bearer)));
        });
        var client = Client(stub);

        var results = await Task.WhenAll(client.GetProductsAsync("token-A", 1, 10, null), client.GetProductsAsync("token-B", 1, 10, null)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, r => Assert.True(r.Status == ApiResultStatus.Success, $"Both overlapping calls must succeed but one was {r.Status}."));
        Assert.Equal("token-A", Assert.Single(results[0].Value!.Items).Name);
        Assert.Equal("token-B", Assert.Single(results[1].Value!.Items).Name);
    }

    [Fact]
    public async Task A_token_used_for_a_product_call_is_not_sent_on_later_calls()
    {
        var stub = new StubHandler(request => request.RequestUri!.AbsolutePath == "/api/auth/login"
            ? Json(HttpStatusCode.OK, new { token = "t", userId = UserId, tenantId = TenantId, isOwner = true })
            : Json(HttpStatusCode.OK, ProductJson()));
        var client = Client(stub);

        await client.GetProductAsync("someone-elses-token", ProductId);
        await client.LoginAsync(new LoginRequest { Email = "a@example.test", Password = "p" });

        Assert.Equal("Bearer someone-elses-token", stub.Calls[0].Authorization);
        Assert.Null(stub.Calls[1].Authorization);
    }

    [Fact]
    public async Task The_callers_own_cancellation_propagates_from_a_product_call()
    {
        using var cancelled = new CancellationTokenSource();
        var stub = new StubHandler(_ =>
        {
            cancelled.Cancel();
            cancelled.Token.ThrowIfCancellationRequested();
            return Respond(HttpStatusCode.OK);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(stub).GetProductsAsync("t", 1, 10, null, cancelled.Token));
    }
}
