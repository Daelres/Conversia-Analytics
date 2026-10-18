using Conversia.TestSupport;

namespace Conversia.IntegrationTests;

/// <summary>
/// The seeded catalogue is presentation material, so its product names are realistic Spanish names. SKUs, prices and stock are
/// facts other suites and the documentation rely on and do not change.
/// </summary>
public class DemoDataSpanishTests(CustomWebApplicationFactory container, DemoClassState state) : DemoDataTestBase(container, state)
{
    /// <summary>The seeded catalogue as it was before the Spanish names: SKU to (price, stock). Only the names change.</summary>
    private static readonly IReadOnlyDictionary<string, (decimal Price, int Stock)> UnchangedFacts = new Dictionary<string, (decimal, int)>
    {
        ["ACME-001"] = (249.90m, 14),
        ["ACME-002"] = (1299.00m, 3),
        ["ACME-003"] = (12.50m, 240),
        ["ACME-004"] = (89.99m, 32),
        ["ACME-005"] = (7.25m, 500),
        ["ACME-006"] = (45.00m, 0),
        ["ACME-007"] = (399.95m, 8),
        ["ACME-008"] = (179.00m, 21),
        ["ACME-009"] = (59.90m, 67),
        ["ACME-010"] = (4.99m, 1200),
        ["ACME-011"] = (24.75m, 0),
        ["ACME-012"] = (119.00m, 15),
        ["GLX-002"] = (19.50m, 300),
        ["GLX-003"] = (64.25m, 40),
        ["GLX-004"] = (429.00m, 0),
        ["GLX-005"] = (9.99m, 1000),
    };

    private static readonly IReadOnlyDictionary<string, (decimal Price, int Stock)> SharedSkuFacts = new Dictionary<string, (decimal, int)>
    {
        ["Acme Demo"] = (35.00m, 90),
        ["Globex Demo"] = (15000.00m, 2),
    };

    /// <summary>The English names the seeder used to create. None of them may survive.</summary>
    private static readonly string[] FormerEnglishNames =
    {
        "Anvil, 50 kg", "Rocket Skates", "Giant Rubber Band", "Portable Hole (pack of 3)", "Dehydrated Boulders", "Earthquake Pills",
        "Giant Magnet", "Jet-Propelled Pogo Stick", "Instant Road", "Iron Bird Seed", "Tornado Seeds", "Spring-Powered Boots",
        "Standard Shipping Crate", "Doomsday Device Casing", "Monorail Ticket Book", "Hypnotic Desk Lamp", "Executive Swivel Chair",
        "Donut Subscription (monthly)",
    };

    /// <summary>English-only words of the former catalogue (loanwords such as pack, jet, ticket or tornado are left out on purpose).</summary>
    private static readonly string[] EnglishProductWords =
    {
        "anvil", "rocket", "skates", "giant", "rubber", "band", "portable", "hole", "dehydrated", "boulders", "earthquake", "pills",
        "magnet", "propelled", "pogo", "stick", "instant", "road", "iron", "bird", "seed", "seeds", "powered", "boots", "standard",
        "shipping", "crate", "doomsday", "device", "casing", "monorail", "book", "hypnotic", "desk", "lamp", "executive", "swivel",
        "chair", "subscription", "monthly", "of", "and", "the", "with", "for",
    };

    private async Task<List<Conversia.Domain.Entities.Product>> SeededProductsAsync()
    {
        var host = await StartEmptyHostAsync();
        await host.SeedAsync();
        var products = await DemoDbReader.ProductsAsync(Db);
        Assert.Equal(DemoData.AcmeProductCount + DemoData.GlobexProductCount, products.Count); // precondition: both catalogues exist
        return products;
    }

    [Fact]
    public async Task No_seeded_product_name_is_one_of_the_former_english_names_or_contains_an_english_word()
    {
        var products = await SeededProductsAsync();

        foreach (var product in products)
        {
            Assert.DoesNotContain(product.Name, FormerEnglishNames);
            Assert.Empty(EnglishLeaks.Find(product.Name));
            var englishWords = System.Text.RegularExpressions.Regex.Matches(product.Name.ToLowerInvariant(), @"\p{L}+").Select(m => m.Value)
                .Where(w => EnglishProductWords.Contains(w)).ToList();
            Assert.True(englishWords.Count == 0, $"Product {product.Sku} '{product.Name}' still uses English words: {string.Join(", ", englishWords)}.");
        }
    }

    [Fact]
    public async Task Seeded_product_names_are_real_names_distinct_within_each_catalogue_and_not_blank_or_placeholders()
    {
        var products = await SeededProductsAsync();

        Assert.All(products, p => Assert.True(p.Name.Trim().Length >= 3 && p.Name == p.Name.Trim(), $"'{p.Name}' is not a presentable name."));
        foreach (var catalogue in products.GroupBy(p => p.TenantId))
        {
            Assert.Equal(catalogue.Count(), catalogue.Select(p => p.Name.ToLowerInvariant()).Distinct().Count());
        }

        Assert.Equal(2, products.Where(p => p.Sku == DemoData.SharedSku).Select(p => p.Name).Distinct().Count()); // the shared SKU names two different products
    }

    [Fact]
    public async Task Seeded_skus_prices_and_stock_are_unchanged()
    {
        var products = await SeededProductsAsync();

        foreach (var (sku, (price, stock)) in UnchangedFacts)
        {
            var product = Assert.Single(products, p => p.Sku == sku);
            Assert.Equal((price, stock), (product.Price, product.Stock));
        }

        var tenants = await DemoDbReader.TenantsAsync(Db);
        foreach (var (tenantName, (price, stock)) in SharedSkuFacts)
        {
            var tenantId = tenants.Single(t => t.Name == tenantName).Id;
            var shared = Assert.Single(products, p => p.TenantId == tenantId && p.Sku == DemoData.SharedSku);
            Assert.Equal((price, stock), (shared.Price, shared.Stock));
        }

        Assert.Equal(
            DemoData.AcmeSkus.Concat(new[] { "GLX-002", "GLX-003", "GLX-004", "GLX-005" }).Order(StringComparer.Ordinal),
            products.Select(p => p.Sku).Distinct().Order(StringComparer.Ordinal)); // the SKU universe is the documented one
    }

    [Fact]
    public async Task The_api_serves_spanish_names_to_both_demo_owners()
    {
        var host = await StartEmptyHostAsync();
        await host.SeedAsync();
        using var acme = await host.SignInAsync(DemoData.AcmeOwnerEmail);
        using var globex = await host.SignInAsync(DemoData.GlobexOwnerEmail);

        var acmeProducts = await DemoApi.AllProductsAsync(acme.Client);
        var globexProducts = await DemoApi.AllProductsAsync(globex.Client);

        Assert.Equal(DemoData.AcmeProductCount, acmeProducts.Count);
        Assert.Equal(DemoData.GlobexProductCount, globexProducts.Count);
        Assert.All(acmeProducts.Concat(globexProducts), p =>
        {
            Assert.Empty(EnglishLeaks.Find(p.Name));
            Assert.DoesNotContain(p.Name, FormerEnglishNames);
        });
    }
}
