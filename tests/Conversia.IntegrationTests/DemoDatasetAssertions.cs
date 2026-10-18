using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.IntegrationTests;

/// <summary>
/// What a database that went through the demo seeding must look like. Every check runs against the whole (private)
/// database, so duplicates, leftovers and orphans fail it as surely as missing rows.
/// </summary>
internal static class DemoDatasetAssertions
{
    /// <summary>Both demo tenants, complete, and nothing else in the database.</summary>
    public static async Task AssertCompleteAsync(DemoDatabase db)
    {
        var tenants = await DemoDbReader.TenantsAsync(db);
        Assert.Equal(
            new[] { DemoData.AcmeTenantName, DemoData.GlobexTenantName }.Order(),
            tenants.Select(t => t.Name).Order());

        var users = await DemoDbReader.UsersAsync(db);
        Assert.Equal(DemoData.AllEmails.Order(), users.Select(u => u.Email).Order());

        await AssertAcmeCompleteAsync(db);
        await AssertGlobexCompleteAsync(db);
        Assert.Equal(DemoData.AcmeProductCount + DemoData.GlobexProductCount, (await DemoDbReader.ProductsAsync(db)).Count);
    }

    /// <summary>The "Acme Demo" tenant with exactly its four users, their permissions and its 13 products (<c>ACME-001</c>..<c>ACME-012</c> and <see cref="DemoData.SharedSku"/>).</summary>
    public static async Task AssertAcmeCompleteAsync(DemoDatabase db)
    {
        var tenantId = await DemoDbReader.TenantIdAsync(db, DemoData.AcmeTenantName);
        var users = (await DemoDbReader.UsersAsync(db)).Where(u => u.TenantId == tenantId).ToList();

        Assert.Equal(DemoData.AcmeEmails.Order(), users.Select(u => u.Email).Order());
        Assert.Equal(DemoData.AcmeUserCount, users.Count);
        Assert.Equal(new[] { DemoData.AcmeOwnerEmail }, users.Where(u => u.IsOwner).Select(u => u.Email));

        Assert.Equal(DemoData.ManagerPermissions.Order(), await DemoDbReader.PermissionsOfAsync(db, DemoData.AcmeManagerEmail));
        Assert.Equal(DemoData.ViewerPermissions.Order(), await DemoDbReader.PermissionsOfAsync(db, DemoData.AcmeViewerEmail));
        Assert.Empty(await DemoDbReader.PermissionsOfAsync(db, DemoData.AcmeNoAccessEmail));

        var products = await DemoDbReader.ProductsAsync(db, tenantId);
        AssertCatalog(products, DemoData.AcmeSkus);
        Assert.Contains(products, p => p.Stock == 0);
        Assert.Contains(products, p => p.Stock > 0);
        Assert.Contains(products, p => p.Price != decimal.Truncate(p.Price));
    }

    /// <summary>The "Globex Demo" tenant with its single owner and its 5 products (one of them <see cref="DemoData.SharedSku"/>).</summary>
    public static async Task AssertGlobexCompleteAsync(DemoDatabase db)
    {
        var tenantId = await DemoDbReader.TenantIdAsync(db, DemoData.GlobexTenantName);
        var users = (await DemoDbReader.UsersAsync(db)).Where(u => u.TenantId == tenantId).ToList();

        var owner = Assert.Single(users);
        Assert.Equal(DemoData.GlobexOwnerEmail, owner.Email);
        Assert.True(owner.IsOwner);

        var products = await DemoDbReader.ProductsAsync(db, tenantId);
        AssertCatalog(products, expectedSkus: null);
        Assert.Equal(DemoData.GlobexProductCount, products.Count);
    }

    /// <summary>
    /// Distinct, valid SKUs including the shared one — exactly <paramref name="expectedSkus"/> when the requirement
    /// names them; names and prices the Product rules accept; a plausible catalogue.
    /// </summary>
    private static void AssertCatalog(IReadOnlyList<Product> products, IReadOnlyCollection<string>? expectedSkus)
    {
        Assert.Equal(products.Count, products.Select(p => p.Sku.ToUpperInvariant()).Distinct().Count());
        Assert.Contains(products, p => p.Sku == DemoData.SharedSku);
        if (expectedSkus is not null)
        {
            Assert.Equal(expectedSkus.Order(StringComparer.Ordinal), products.Select(p => p.Sku).Order(StringComparer.Ordinal));
        }

        Assert.All(products, p =>
        {
            Assert.True(Product.IsValidSku(p.Sku), $"SKU '{p.Sku}' is not a valid SKU.");
            Assert.True(Product.IsValidName(p.Name), $"Product '{p.Sku}' has an invalid name.");
            Assert.True(Product.IsValidPrice(p.Price), $"Product '{p.Sku}' has an invalid price {p.Price}.");
            Assert.True(p.Stock >= 0);
        });
    }

    /// <summary>
    /// Whatever the database holds of the demo tenants is complete or absent — never a tenant without its owner,
    /// products or permissions — and nothing outside the planted rows is left over from an attempt that was refused.
    /// </summary>
    public static async Task AssertNoHalfSeededTenantAsync(DemoDatabase db)
    {
        var tenants = await DemoDbReader.TenantsAsync(db);
        var users = await DemoDbReader.UsersAsync(db);
        var products = await DemoDbReader.ProductsAsync(db);

        Assert.All(tenants, t => Assert.Contains(users, u => u.TenantId == t.Id));
        Assert.All(products, p => Assert.Contains(tenants, t => t.Id == p.TenantId));

        if (tenants.Any(t => t.Name == DemoData.AcmeTenantName))
        {
            await AssertAcmeCompleteAsync(db);
        }
        else
        {
            Assert.DoesNotContain(users, u => u.Email == DemoData.AcmeOwnerEmail);
        }

        if (tenants.Any(t => t.Name == DemoData.GlobexTenantName))
        {
            await AssertGlobexCompleteAsync(db);
        }
        else
        {
            Assert.DoesNotContain(users, u => u.Email == DemoData.GlobexOwnerEmail);
        }

        Assert.InRange(tenants.Count(t => t.Name == DemoData.AcmeTenantName), 0, 1);
        Assert.InRange(tenants.Count(t => t.Name == DemoData.GlobexTenantName), 0, 1);
    }

    /// <summary>Every demo account's stored hash is a real hash of the demo password and of nothing else.</summary>
    public static async Task AssertPasswordsHashedAsync(DemoHost host, DemoDatabase db, string password)
    {
        using var scope = host.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasherService>();

        foreach (var user in await DemoDbReader.UsersAsync(db))
        {
            Assert.NotEqual(password, user.PasswordHash);
            Assert.DoesNotContain(password, user.PasswordHash);
            Assert.True(hasher.VerifyPassword(user.PasswordHash, password), $"{user.Email} does not verify with the demo password.");
            Assert.False(hasher.VerifyPassword(user.PasswordHash, password + "x"), $"{user.Email} verifies with a different password.");
        }
    }
}
