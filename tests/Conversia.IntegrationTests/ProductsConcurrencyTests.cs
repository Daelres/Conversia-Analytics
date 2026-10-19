using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Products;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Multi-user behaviour of <c>/api/products</c> against the real PostgreSQL: the unique <c>(TenantId, Sku)</c> index
/// and the row-version token decide races, so contenders get exactly one winner and controlled 4xx answers for the
/// rest — never a 5xx, never a duplicate row, never another tenant's data.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ProductsConcurrencyTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProductsConcurrencyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Func<Task<HttpResponseMessage>> Call(string token, Func<HttpClient, Task<HttpResponseMessage>> request) => async () =>
    {
        using var client = AuthorizedClient(_factory, token);
        return await request(client);
    };

    private Func<Task<HttpResponseMessage>> Create(string token, string name, string sku) => Call(token, c => PostProductAsync(c, name, sku, 1m, 1));

    private static void AssertNoTransportFailureOrServerError(CallResult[] results)
    {
        foreach (var result in results)
        {
            Assert.True(result.Status is not null, $"Transport failure: {result.Body}");
            Assert.True((int)result.Status! < 500, $"Server error {(int)result.Status}: {result.Body}");
        }
    }

    private static void AssertExactlyOneCreatedRestConflict(CallResult[] results)
    {
        AssertNoTransportFailureOrServerError(results);
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(results.Length - 1, results.Count(r => r.Status == HttpStatusCode.Conflict));
    }

    // ---- Unique SKU per tenant ---------------------------------------------------------------

    [Fact]
    public async Task Create_20ParallelRequestsWithTheSameSku_ExactlyOneSucceedsAndTheRestConflict()
    {
        using var tenant = await StartTenantAsync(_factory, _client);
        var sku = UniqueSku("race");

        var results = await RunInParallelWithBodiesAsync(
            Enumerable.Range(0, 20).Select(i => Create(tenant.Owner.Token, $"Racer {i}", sku)));

        AssertExactlyOneCreatedRestConflict(results);
        var winner = results.Single(r => r.Status == HttpStatusCode.Created).Product!;
        var row = Assert.Single(await FindProductsBySkuAsync(_factory, sku));
        Assert.Equal(winner.Id, row.Id);
        Assert.Equal(winner.Name, row.Name);
        Assert.Equal(tenant.TenantId, row.TenantId);
        Assert.Equal(1, (await ListProductsOkAsync(tenant.Owner.Client)).TotalCount);
    }

    [Fact]
    public async Task Create_ParallelRequestsWhoseSkusDifferOnlyInCaseAndWhitespace_ExactlyOneSucceeds()
    {
        using var tenant = await StartTenantAsync(_factory, _client);
        var sku = UniqueSku("Mixed");
        var spellings = new[] { sku, sku.ToUpperInvariant(), sku.ToLowerInvariant(), $"  {sku}  ", $"\t{sku.ToUpperInvariant()}\n" };

        var results = await RunInParallelWithBodiesAsync(
            Enumerable.Range(0, 25).Select(i => Create(tenant.Owner.Token, $"Racer {i}", spellings[i % spellings.Length])));

        AssertExactlyOneCreatedRestConflict(results);
        Assert.Single(await FindProductsBySkuAsync(_factory, sku));
    }

    [Fact]
    public async Task Create_TheSameSkuInTwoTenantsAtOnce_SucceedsInBoth()
    {
        using var a = await StartTenantAsync(_factory, _client, "a");
        using var b = await StartTenantAsync(_factory, _client, "b");
        var sku = UniqueSku("twin");

        var results = await RunInParallelWithBodiesAsync(new[]
        {
            Create(a.Owner.Token, "From A", sku),
            Create(b.Owner.Token, "From B", sku),
        });

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        var rows = await FindProductsBySkuAsync(_factory, sku);
        Assert.Equal(new[] { a.TenantId, b.TenantId }.Order(), rows.Select(r => r.TenantId).Order());
    }

    [Fact]
    public async Task Create_ManyParallelRequestsWithTheSameSkuInTwoTenants_EachTenantHasExactlyOneWinner()
    {
        using var a = await StartTenantAsync(_factory, _client, "a");
        using var b = await StartTenantAsync(_factory, _client, "b");
        var sku = UniqueSku("duel");
        var calls = Enumerable.Range(0, 20)
            .Select(i => Create(i % 2 == 0 ? a.Owner.Token : b.Owner.Token, $"Racer {i}", sku))
            .ToList();

        var results = await RunInParallelWithBodiesAsync(calls);

        AssertNoTransportFailureOrServerError(results);
        var evenResults = results.Where((_, i) => i % 2 == 0).ToArray(); // tenant A's calls
        var oddResults = results.Where((_, i) => i % 2 == 1).ToArray();  // tenant B's calls
        AssertExactlyOneCreatedRestConflict(evenResults);
        AssertExactlyOneCreatedRestConflict(oddResults);
        var rows = await FindProductsBySkuAsync(_factory, sku);
        Assert.Equal(new[] { a.TenantId, b.TenantId }.Order(), rows.Select(r => r.TenantId).Order());
    }

    [Fact]
    public async Task Create_30ParallelRequestsWithDistinctSkus_AllSucceedAndAllLandInTheCallersTenant()
    {
        using var tenant = await StartTenantAsync(_factory, _client);
        var skus = Enumerable.Range(0, 30).Select(_ => UniqueSku("load")).ToList();

        var results = await RunInParallelWithBodiesAsync(skus.Select((sku, i) => Create(tenant.Owner.Token, $"Load {i:D2}", sku)));

        AssertNoTransportFailureOrServerError(results);
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        var listed = await ListProductsOkAsync(tenant.Owner.Client, "pageSize=100");
        Assert.Equal(30, listed.TotalCount);
        Assert.Equal(30, listed.Items.Select(p => p.Id).Distinct().Count());
        Assert.Equal(skus.Order(StringComparer.Ordinal), listed.Items.Select(p => p.Sku).Order(StringComparer.Ordinal));
        Assert.Equal(30, results.Select(r => r.Product!.Id).Distinct().Count());
    }

    [Fact]
    public async Task Create_TwoCallersInParallel_EachProductRecordsItsOwnCreator()
    {
        using var tenant = await StartTenantAsync(_factory, _client);
        using var manager = await tenant.AddMemberAsync("manager", Permission.ProductsManage);

        var results = await RunInParallelWithBodiesAsync(
            Enumerable.Range(0, 10).Select(i => Create(tenant.Owner.Token, $"By owner {i}", UniqueSku("o")))
                .Concat(Enumerable.Range(0, 10).Select(i => Create(manager.Token, $"By manager {i}", UniqueSku("m")))));

        AssertNoTransportFailureOrServerError(results);
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.All(results.Take(10), r => Assert.Equal(tenant.Owner.UserId, r.Product!.CreatedBy));
        Assert.All(results.Skip(10), r => Assert.Equal(manager.UserId, r.Product!.CreatedBy));
        foreach (var result in results)
        {
            Assert.Equal(result.Product!.CreatedBy, (await FindProductByIdAsync(_factory, result.Product.Id))!.CreatedBy);
        }
    }

    // ---- Optimistic concurrency ----------------------------------------------------------------

    [Fact]
    public async Task Update_10ParallelRequestsWithTheSameRowVersion_ExactlyOneWinsAndThePersistedRowIsTheWinners()
    {
        using var tenant = await StartTenantAsync(_factory, _client);

        for (var round = 0; round < 3; round++)
        {
            var original = await CreateProductAsync(tenant.Owner.Client, $"Original {round}", price: 1m, stock: 1);

            var results = await RunInParallelWithBodiesAsync(Enumerable.Range(0, 10).Select(i =>
                Call(tenant.Owner.Token, c => PutProductAsync(c, original.Id, $"Contender {round}-{i}", 100m + i, 100 + i, original.RowVersion))));

            AssertNoTransportFailureOrServerError(results);
            Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
            Assert.Equal(9, results.Count(r => r.Status == HttpStatusCode.Conflict));
            var winner = results.Single(r => r.Status == HttpStatusCode.OK).Product!;
            Assert.StartsWith($"Contender {round}-", winner.Name);
            Assert.NotEqual(original.RowVersion, winner.RowVersion);
            var persisted = await GetProductOkAsync(tenant.Owner.Client, original.Id);
            AssertSameProduct(winner, persisted);
        }
    }

    [Fact]
    public async Task Update_ParallelRequestsFromTwoUsers_ExactlyOneWinsAndUpdatedByNamesTheWinner()
    {
        using var tenant = await StartTenantAsync(_factory, _client);
        using var editor = await tenant.AddMemberAsync("editor", Permission.ProductsManage);
        var original = await CreateProductAsync(tenant.Owner.Client);
        var tokens = new[] { tenant.Owner.Token, editor.Token };

        var results = await RunInParallelWithBodiesAsync(Enumerable.Range(0, 8).Select(i =>
            Call(tokens[i % 2], c => PutProductAsync(c, original.Id, $"Edit {i}", 10m + i, i, original.RowVersion))));

        AssertNoTransportFailureOrServerError(results);
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(r => r.Status == HttpStatusCode.Conflict));
        var winnerIndex = Array.FindIndex(results, r => r.Status == HttpStatusCode.OK);
        var persisted = await GetProductOkAsync(tenant.Owner.Client, original.Id);
        AssertSameProduct(results[winnerIndex].Product!, persisted);
        Assert.Equal(winnerIndex % 2 == 0 ? tenant.Owner.UserId : editor.UserId, persisted.UpdatedBy);
    }

    [Fact]
    public async Task Update_SequentialWritersWhoReloadBetweenWrites_NeverConflict()
    {
        using var tenant = await StartTenantAsync(_factory, _client);
        var product = await CreateProductAsync(tenant.Owner.Client);

        for (var i = 0; i < 5; i++)
        {
            product = await UpdateProductOkAsync(tenant.Owner.Client, await GetProductOkAsync(tenant.Owner.Client, product.Id), $"Step {i}");
        }

        Assert.Equal("Step 4", (await GetProductOkAsync(tenant.Owner.Client, product.Id)).Name);
    }

    [Fact]
    public async Task UpdateRacingDelete_NeverFailsWithA5xx_AndTheOutcomesAgreeWithTheFinalState()
    {
        using var tenant = await StartTenantAsync(_factory, _client);

        for (var round = 0; round < 8; round++)
        {
            var original = await CreateProductAsync(tenant.Owner.Client, $"Racing {round}");
            var calls = Enumerable.Range(0, 3)
                .Select(i => Call(tenant.Owner.Token, c => PutProductAsync(c, original.Id, $"Update {round}-{i}", 50m + i, 50 + i, original.RowVersion)))
                .Append(Call(tenant.Owner.Token, c => DeleteProductAsync(c, original.Id)))
                .ToList();

            var results = await RunInParallelWithBodiesAsync(calls);

            AssertNoTransportFailureOrServerError(results);
            var updates = results.Take(3).ToArray();
            var delete = results[3];
            Assert.All(updates, u => Assert.Contains(u.Status!.Value, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.Conflict }));
            Assert.Contains(delete.Status!.Value, new[] { HttpStatusCode.NoContent, HttpStatusCode.NotFound, HttpStatusCode.Conflict });

            var final = await GetProductAsync(tenant.Owner.Client, original.Id);
            var successfulUpdates = updates.Where(u => u.Status == HttpStatusCode.OK).ToArray();
            Assert.True(successfulUpdates.Length <= 1, "Updates sharing one row version cannot both succeed.");
            if (final.StatusCode == HttpStatusCode.NotFound)
            {
                Assert.Equal(HttpStatusCode.NoContent, delete.Status); // gone, so the delete is what removed it
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, final.StatusCode);
                Assert.NotEqual(HttpStatusCode.NoContent, delete.Status); // still there, so no delete succeeded
                var winner = Assert.Single(successfulUpdates); // and the delete only lost because an update won
                AssertSameProduct(winner.Product!, await ReadProductAsync(final));
            }
        }
    }

    [Fact]
    public async Task Delete_5ParallelRequestsForTheSameProduct_ExactlyOneSucceedsAndTheRestAre404()
    {
        using var tenant = await StartTenantAsync(_factory, _client);
        var product = await CreateProductAsync(tenant.Owner.Client);

        var results = await RunInParallelWithBodiesAsync(
            Enumerable.Range(0, 5).Select(_ => Call(tenant.Owner.Token, c => DeleteProductAsync(c, product.Id))));

        AssertNoTransportFailureOrServerError(results);
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.NoContent));
        Assert.Equal(4, results.Count(r => r.Status == HttpStatusCode.NotFound));
        Assert.Equal(HttpStatusCode.NotFound, (await GetProductAsync(tenant.Owner.Client, product.Id)).StatusCode);
        Assert.Null(await FindProductByIdAsync(_factory, product.Id));
    }

    [Fact]
    public async Task DeleteThenRecreateWithTheSameSku_IsolatesTheOldVersionFromTheNewOne()
    {
        using var tenant = await StartTenantAsync(_factory, _client);
        var http = tenant.Owner.Client;
        var sku = UniqueSku("phoenix");
        var old = await CreateProductAsync(http, "Old", sku);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteProductAsync(http, old.Id)).StatusCode);
        var reborn = await CreateProductAsync(http, "Reborn", sku);

        var staleWrite = await PutProductAsync(http, old.Id, "Zombie", 1m, 1, old.RowVersion);

        Assert.Equal(HttpStatusCode.NotFound, staleWrite.StatusCode);
        await AssertProductUnchangedAsync(http, reborn);
        var viaNewId = await PutProductAsync(http, reborn.Id, "Zombie", 1m, 1, old.RowVersion);
        Assert.Equal(HttpStatusCode.Conflict, viaNewId.StatusCode); // the old row version means nothing for the new row
        await AssertProductUnchangedAsync(http, reborn);
    }

    // ---- Readers and writers across tenants ----------------------------------------------------

    [Fact]
    public async Task ConcurrentReadersAndWritersOfSeveralTenants_NeverSeeEachOthersProducts()
    {
        const int tenantCount = 4;
        const int seeded = 5;
        const int extraCreates = 6;
        var tenants = new List<TestTenant>();
        var seededIds = new List<List<Guid>>();
        try
        {
            for (var t = 0; t < tenantCount; t++)
            {
                var tenant = await StartTenantAsync(_factory, _client, $"t{t}");
                tenants.Add(tenant);
                var ids = new List<Guid>();
                for (var i = 0; i < seeded; i++)
                {
                    ids.Add((await CreateProductAsync(tenant.Owner.Client, $"T{t}-seed-{i}", $"T{t}-SEED-{i}")).Id);
                }

                seededIds.Add(ids);
            }

            var tasks = new List<Task<string?>>();
            for (var t = 0; t < tenantCount; t++)
            {
                var index = t;
                var token = tenants[t].Owner.Token;
                var foreignIds = Enumerable.Range(0, tenantCount).Where(o => o != index).SelectMany(o => seededIds[o]).ToList();

                for (var i = 0; i < extraCreates; i++)
                {
                    var k = i;
                    tasks.Add(Violation(async () =>
                    {
                        using var c = AuthorizedClient(_factory, token);
                        var r = await PostProductAsync(c, $"T{index}-new-{k}", $"T{index}-NEW-{k}");
                        return r.StatusCode == HttpStatusCode.Created ? null : $"create returned {(int)r.StatusCode}";
                    }));
                }

                for (var i = 0; i < 3; i++)
                {
                    var id = seededIds[index][i];
                    tasks.Add(Violation(async () =>
                    {
                        using var c = AuthorizedClient(_factory, token);
                        var current = await GetProductAsync(c, id);
                        if (current.StatusCode != HttpStatusCode.OK)
                        {
                            return $"own get returned {(int)current.StatusCode}";
                        }

                        var product = await ReadProductAsync(current);
                        var r = await PutProductAsync(c, id, product.Name + "!", product.Price, product.Stock, product.RowVersion);
                        return r.StatusCode == HttpStatusCode.OK ? null : $"own update returned {(int)r.StatusCode}";
                    }));
                }

                for (var i = 0; i < 12; i++)
                {
                    tasks.Add(Violation(async () =>
                    {
                        using var c = AuthorizedClient(_factory, token);
                        var r = await ListProductsAsync(c, "pageSize=100");
                        if (r.StatusCode != HttpStatusCode.OK)
                        {
                            return $"list returned {(int)r.StatusCode}";
                        }

                        var page = (await r.Content.ReadFromJsonAsync<PagedResponse<ProductResponse>>())!;
                        var foreign = page.Items.FirstOrDefault(p => !p.Name.StartsWith($"T{index}-") || !p.Sku.StartsWith($"T{index}-"));
                        if (foreign is not null)
                        {
                            return $"tenant {index} saw foreign product '{foreign.Name}' / '{foreign.Sku}'";
                        }

                        return page.TotalCount is < seeded or > seeded + extraCreates
                            ? $"tenant {index} total {page.TotalCount} outside {seeded}..{seeded + extraCreates}"
                            : null;
                    }));
                }

                for (var i = 0; i < 8; i++)
                {
                    var foreignId = foreignIds[i % foreignIds.Count];
                    tasks.Add(Violation(async () =>
                    {
                        using var c = AuthorizedClient(_factory, token);
                        var get = await GetProductAsync(c, foreignId);
                        var delete = await DeleteProductAsync(c, foreignId);
                        return get.StatusCode == HttpStatusCode.NotFound && delete.StatusCode == HttpStatusCode.NotFound
                            ? null
                            : $"tenant {index} reached a foreign product: get {(int)get.StatusCode}, delete {(int)delete.StatusCode}";
                    }));
                }
            }

            var violations = (await Task.WhenAll(tasks)).Where(v => v is not null).ToList();

            Assert.Empty(violations);
            for (var t = 0; t < tenantCount; t++)
            {
                var final = await ListProductsOkAsync(tenants[t].Owner.Client, "pageSize=100");
                Assert.Equal(seeded + extraCreates, final.TotalCount);
                Assert.Equal(seeded + extraCreates, final.Items.Count);
                Assert.All(final.Items, p => Assert.StartsWith($"T{t}-", p.Name));
                Assert.Equal(seeded, final.Items.Count(p => p.Name.Contains("-seed-")));
            }
        }
        finally
        {
            tenants.ForEach(t => t.Dispose());
        }
    }

    private static async Task<string?> Violation(Func<Task<string?>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception ex)
        {
            return $"threw {ex.GetType().Name}: {ex.Message}";
        }
    }
}
