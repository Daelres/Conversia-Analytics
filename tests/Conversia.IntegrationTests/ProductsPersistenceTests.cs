using System.Security.Claims;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The Products table as created by the migrations on a real PostgreSQL, and the behaviour
/// <see cref="ApplicationDbContext"/> gives <see cref="Product"/> by itself: unique <c>(TenantId, Sku)</c> index,
/// the tenant query filter, central audit stamping and the row-version concurrency token (the <c>xmin</c> system column).
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ProductsPersistenceTests
{
    private const string JwtSub = "sub";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProductsPersistenceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>A tenant that really exists, so the tests hold whether or not Products carries a foreign key to Tenants.</summary>
    private async Task<Guid> NewTenantIdAsync() => (await RegisterTenantSessionAsync(_client, "persist")).TenantId;

    private sealed class FixedTenant(Guid tenantId) : ICurrentTenantService
    {
        public Guid TenantId { get; } = tenantId;
    }

    /// <summary>A context for the given tenant and acting user, without any HTTP request around it.</summary>
    private (ApplicationDbContext Context, IServiceScope Scope) OpenContext(Guid tenantId, Guid? actingUser = null)
    {
        var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<ApplicationDbContext>>();
        IHttpContextAccessor? accessor = null;
        if (actingUser is not null)
        {
            accessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(JwtSub, actingUser.Value.ToString()) }, "test")),
                },
            };
        }

        return (new ApplicationDbContext(options, new FixedTenant(tenantId), accessor), scope);
    }

    private static Product NewProduct(Guid tenantId, string? sku = null, string name = "Persisted")
        => Product.Create(tenantId, name, sku ?? UniqueSku("db"), 9.99m, 4);

    // ---- Schema ------------------------------------------------------------------------------

    [Fact]
    public async Task TheInitialCreateMigration_IsAppliedAndTheModelHasNoPendingChanges()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();

        Assert.Contains(applied, m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges(), "The model differs from the last migration snapshot: a migration is missing.");
    }

    [Fact]
    public async Task TheProductsTable_HasAUniqueIndexOnTenantIdThenSku()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var uniqueIndexes = await db.Database.SqlQueryRaw<string>(
            """
            SELECT string_agg(a.attname, ',' ORDER BY k.ord) AS "Value"
            FROM pg_index ix
            JOIN pg_class t ON t.oid = ix.indrelid
            CROSS JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord)
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE t.relname = 'Products' AND t.relnamespace = 'public'::regnamespace AND ix.indisunique AND NOT ix.indisprimary
            GROUP BY ix.indexrelid
            """).ToListAsync();

        Assert.Contains("TenantId,Sku", uniqueIndexes);
    }

    // ---- Unique (TenantId, Sku) --------------------------------------------------------------

    [Fact]
    public async Task TheDatabase_RefusesTheSameSkuTwiceInOneTenant_EvenWhenOnlyTheCaseDiffers_ButAllowsItInAnother()
    {
        var tenantA = await NewTenantIdAsync();
        var tenantB = await NewTenantIdAsync();
        var sku = UniqueSku("Idx");
        var (db, scope) = OpenContext(tenantA);
        using (scope)
        using (db)
        {
            db.Set<Product>().Add(NewProduct(tenantA, sku));
            db.Set<Product>().Add(NewProduct(tenantB, sku)); // another tenant, same SKU
            await db.SaveChangesAsync();
        }

        var (second, secondScope) = OpenContext(tenantA);
        using (secondScope)
        using (second)
        {
            second.Set<Product>().Add(NewProduct(tenantA, sku.ToUpperInvariant()));
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        }

        Assert.Equal(2, (await FindProductsBySkuAsync(_factory, sku)).Count);
    }

    // ---- Tenant query filter -------------------------------------------------------------------

    [Fact]
    public async Task TheQueryFilter_HidesOtherTenantsProductsFromReadsAndFromSetBasedWrites()
    {
        var tenantA = await NewTenantIdAsync();
        var tenantB = await NewTenantIdAsync();
        var inA = NewProduct(tenantA, UniqueSku("fa"), "In A");
        var inB = NewProduct(tenantB, UniqueSku("fb"), "In B");
        var (seed, seedScope) = OpenContext(tenantA);
        using (seedScope)
        using (seed)
        {
            seed.Set<Product>().AddRange(inA, inB);
            await seed.SaveChangesAsync();
        }

        var (asA, scopeA) = OpenContext(tenantA);
        using (scopeA)
        using (asA)
        {
            var visible = await asA.Set<Product>().AsNoTracking().Where(p => p.Id == inA.Id || p.Id == inB.Id).ToListAsync();

            Assert.Equal(inA.Id, Assert.Single(visible).Id);
            Assert.Null(await asA.Set<Product>().FirstOrDefaultAsync(p => p.Id == inB.Id));
            Assert.Equal(0, await asA.Set<Product>().Where(p => p.Id == inB.Id).ExecuteDeleteAsync());
            Assert.Equal(2, await asA.Set<Product>().IgnoreQueryFilters().CountAsync(p => p.Id == inA.Id || p.Id == inB.Id));
        }

        var (asNobody, scopeNobody) = OpenContext(Guid.Empty);
        using (scopeNobody)
        using (asNobody)
        {
            Assert.Empty(await asNobody.Set<Product>().Where(p => p.Id == inA.Id || p.Id == inB.Id).ToListAsync());
        }
    }

    // ---- Central audit stamping and the concurrency token ---------------------------------------

    [Fact]
    public async Task SavingAProduct_StampsAuditFieldsCentrallyWithTheActingUser()
    {
        var tenant = await NewTenantIdAsync();
        var creator = Guid.NewGuid();
        var updater = Guid.NewGuid();
        var product = NewProduct(tenant);
        var before = DateTime.UtcNow.AddMinutes(-1);

        var (create, createScope) = OpenContext(tenant, creator);
        using (createScope)
        using (create)
        {
            create.Set<Product>().Add(product);
            await create.SaveChangesAsync();
        }

        var (update, updateScope) = OpenContext(tenant, updater);
        using (updateScope)
        using (update)
        {
            var loaded = await update.Set<Product>().SingleAsync(p => p.Id == product.Id);
            Assert.Equal(creator, loaded.CreatedBy);
            Assert.InRange(loaded.CreatedAt, before, DateTime.UtcNow.AddMinutes(1));
            Assert.Null(loaded.UpdatedAt);
            loaded.Update("Renamed", 1m, 1);
            await update.SaveChangesAsync();
        }

        var stored = (await FindProductByIdAsync(_factory, product.Id))!;
        Assert.Equal(creator, stored.CreatedBy);
        Assert.Equal(updater, stored.UpdatedBy);
        Assert.NotNull(stored.UpdatedAt);
        Assert.True(stored.UpdatedAt >= stored.CreatedAt);
    }

    [Fact]
    public async Task TheRowVersion_IsGeneratedOnInsertChangesOnEveryUpdate_AndGuardsAgainstLostUpdates()
    {
        var tenant = await NewTenantIdAsync();
        var product = NewProduct(tenant);
        var (first, firstScope) = OpenContext(tenant);
        var (second, secondScope) = OpenContext(tenant);
        using (firstScope)
        using (secondScope)
        using (first)
        using (second)
        {
            first.Set<Product>().Add(product);
            await first.SaveChangesAsync();
            Assert.NotEqual(0u, product.RowVersion); // xmin, read back with RETURNING
            var inserted = product.RowVersion;

            var loadedByFirst = await first.Set<Product>().SingleAsync(p => p.Id == product.Id);
            var loadedBySecond = await second.Set<Product>().SingleAsync(p => p.Id == product.Id);
            loadedByFirst.Update("First writer", 1m, 1);
            await first.SaveChangesAsync();
            Assert.NotEqual(inserted, loadedByFirst.RowVersion);

            loadedBySecond.Update("Second writer", 2m, 2);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        }

        var stored = (await FindProductByIdAsync(_factory, product.Id))!;
        Assert.Equal("First writer", stored.Name);
    }
}
