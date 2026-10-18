using Conversia.Application.DTOs.Products;
using Conversia.Application.Exceptions;
using Conversia.Application.Services;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>
/// Observable contract of <see cref="ProductService"/> against a mocked <see cref="IProductRepository"/>: what it
/// returns, what it throws and what it hands to the store. Tenant isolation itself (the query filter) is the
/// repository's job and is covered by the integration tests.
/// </summary>
public class ProductServiceTests
{
    private const uint StoredRowVersion = 2001;
    private const uint NextRowVersion = 2002;

    private readonly Mock<IProductRepository> _repository = new();
    private readonly Guid _callerTenantId = Guid.NewGuid();

    private ProductService CreateSut() => new(_repository.Object);

    private Product StoredProduct(string name = "Widget Pro", string sku = "WID-001", decimal price = 12.34m, int stock = 5)
    {
        var product = Product.Create(_callerTenantId, name, sku, price, stock);
        product.RowVersion = StoredRowVersion;
        product.CreatedAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        product.CreatedBy = Guid.NewGuid();
        return product;
    }

    private static CreateProductRequest CreateRequest(string name = "Widget Pro", string sku = "WID-001", decimal price = 12.34m, int stock = 5)
        => new() { Name = name, Sku = sku, Price = price, Stock = stock };

    private static UpdateProductRequest UpdateRequest(string name = "Widget Max", decimal price = 20m, int stock = 7, uint? rowVersion = null)
        => new() { Name = name, Price = price, Stock = stock, RowVersion = RowVersionToken.Encode(rowVersion ?? StoredRowVersion) };

    private void StoreHolds(Product product)
    {
        _repository.Setup(r => r.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);
        _repository.Setup(r => r.GetForUpdateAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);
    }

    // ---- List ---------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_ReturnsTheRepositoryPageMappedAndEchoesPagingAndTotal()
    {
        var first = StoredProduct("Alpha", "AAA-1", 1.5m, 2);
        var second = StoredProduct("Beta", "BBB-1", 2.5m, 3);
        _repository.Setup(r => r.ListAsync(It.IsAny<string?>(), 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new[] { first, second }, 12));

        var response = await CreateSut().ListAsync(new ProductQuery { Page = 2, PageSize = 10 });

        Assert.Equal(2, response.Page);
        Assert.Equal(10, response.PageSize);
        Assert.Equal(12, response.TotalCount);
        Assert.Collection(response.Items,
            a =>
            {
                Assert.Equal(first.Id, a.Id);
                Assert.Equal("Alpha", a.Name);
                Assert.Equal("AAA-1", a.Sku);
                Assert.Equal(1.5m, a.Price);
                Assert.Equal(2, a.Stock);
                Assert.Equal(RowVersionToken.Encode(StoredRowVersion), a.RowVersion);
                Assert.Equal(first.CreatedAt, a.CreatedAt);
                Assert.Equal(first.CreatedBy, a.CreatedBy);
            },
            b => Assert.Equal(second.Id, b.Id));
    }

    [Fact]
    public async Task ListAsync_WithNoProducts_ReturnsEmptyItemsAndZeroTotal()
    {
        _repository.Setup(r => r.ListAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<Product>(), 0));

        var response = await CreateSut().ListAsync(new ProductQuery());

        Assert.Empty(response.Items);
        Assert.Equal(0, response.TotalCount);
        Assert.Equal(1, response.Page);
        Assert.Equal(20, response.PageSize);
    }

    [Fact]
    public async Task ListAsync_HandsTheSearchTermToTheStoreUnchangedExceptForSurroundingWhitespace()
    {
        string? seen = "not called";
        _repository.Setup(r => r.ListAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string?, int, int, CancellationToken>((search, _, _, _) => seen = search)
            .ReturnsAsync((Array.Empty<Product>(), 0));

        await CreateSut().ListAsync(new ProductQuery { Search = "100%_[a]" });

        Assert.Equal("100%_[a]", seen?.Trim());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListAsync_WithBlankSearch_AsksTheStoreForNoFilter(string? search)
    {
        string? seen = "not called";
        _repository.Setup(r => r.ListAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string?, int, int, CancellationToken>((s, _, _, _) => seen = s)
            .ReturnsAsync((Array.Empty<Product>(), 0));

        await CreateSut().ListAsync(new ProductQuery { Search = search });

        Assert.NotEqual("not called", seen);
        Assert.True(string.IsNullOrWhiteSpace(seen), $"A blank search must not reach the store as a filter, got '{seen}'.");
    }

    // ---- Get ----------------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ForAnExistingProduct_ReturnsItMapped()
    {
        var product = StoredProduct();
        StoreHolds(product);

        var response = await CreateSut().GetAsync(product.Id);

        Assert.Equal(product.Id, response.Id);
        Assert.Equal("Widget Pro", response.Name);
        Assert.Equal("WID-001", response.Sku);
        Assert.Equal(12.34m, response.Price);
        Assert.Equal(5, response.Stock);
        Assert.Equal(RowVersionToken.Encode(StoredRowVersion), response.RowVersion);
        Assert.Null(response.UpdatedAt);
        Assert.Null(response.UpdatedBy);
    }

    [Fact]
    public async Task GetAsync_WhenTheStoreHasNoSuchProduct_ThrowsProductNotFoundException()
    {
        var existing = StoredProduct();
        StoreHolds(existing); // the store is live: only the unknown id is missing

        Assert.Equal(existing.Id, (await CreateSut().GetAsync(existing.Id)).Id);
        await Assert.ThrowsAsync<ProductNotFoundException>(() => CreateSut().GetAsync(Guid.NewGuid()));
    }

    // ---- Create -------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PersistsTheProductInTheCallersTenantWithTheTrimmedSku_AndReturnsTheStoredState()
    {
        Product? persisted = null;
        var createdBy = Guid.NewGuid();
        var createdAt = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        _repository.Setup(r => r.AddAsync(It.IsAny<Product>(), It.IsAny<CancellationToken>()))
            .Callback<Product, CancellationToken>((p, _) =>
            {
                // What the database and the audit stamping add while saving.
                p.RowVersion = StoredRowVersion;
                p.CreatedAt = createdAt;
                p.CreatedBy = createdBy;
                persisted = p;
            })
            .Returns(Task.CompletedTask);

        var response = await CreateSut().CreateAsync(_callerTenantId, CreateRequest(sku: "  WID-001  "));

        Assert.NotNull(persisted);
        Assert.Equal(_callerTenantId, persisted!.TenantId);
        Assert.Equal("Widget Pro", persisted.Name);
        Assert.Equal("WID-001", persisted.Sku, ignoreCase: true);
        Assert.Equal("WID-001".Length, persisted.Sku.Length);
        Assert.Equal(12.34m, persisted.Price);
        Assert.Equal(5, persisted.Stock);
        _repository.Verify(r => r.AddAsync(It.IsAny<Product>(), It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(persisted.Id, response.Id);
        Assert.Equal(persisted.Sku, response.Sku);
        Assert.Equal(12.34m, response.Price);
        Assert.Equal(5, response.Stock);
        Assert.Equal(RowVersionToken.Encode(StoredRowVersion), response.RowVersion);
        Assert.Equal(createdAt, response.CreatedAt);
        Assert.Equal(createdBy, response.CreatedBy);
        Assert.Null(response.UpdatedAt);
    }

    [Fact]
    public async Task CreateAsync_WhenTheStoreReportsADuplicateSku_ThrowsDuplicateSkuException()
    {
        _repository.Setup(r => r.AddAsync(It.IsAny<Product>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DuplicateSkuException());

        await Assert.ThrowsAsync<DuplicateSkuException>(() => CreateSut().CreateAsync(_callerTenantId, CreateRequest()));
    }

    public static TheoryData<string, string, decimal, int> RequestsTheDomainRejects => new()
    {
        { "", "WID-001", 1m, 1 },
        { "   ", "WID-001", 1m, 1 },
        { new string('n', 201), "WID-001", 1m, 1 },
        { "Widget", "", 1m, 1 },
        { "Widget", "has space", 1m, 1 },
        { "Widget", new string('S', 51), 1m, 1 },
        { "Widget", "WID-001", -0.01m, 1 },
        { "Widget", "WID-001", 1000000000m, 1 },
        { "Widget", "WID-001", 1.005m, 1 },
        { "Widget", "WID-001", 1m, -1 },
    };

    [Theory]
    [MemberData(nameof(RequestsTheDomainRejects))]
    public async Task CreateAsync_WithValuesTheDomainRejects_ThrowsDomainExceptionAndWritesNothing(string name, string sku, decimal price, int stock)
    {
        // Defence in depth: even when boundary validation is bypassed the outcome is a DomainException (400), never a stored row.
        await Assert.ThrowsAsync<DomainException>(
            () => CreateSut().CreateAsync(_callerTenantId, CreateRequest(name, sku, price, stock)));

        _repository.Verify(r => r.AddAsync(It.IsAny<Product>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WithoutATenant_ThrowsDomainExceptionAndWritesNothing()
    {
        await Assert.ThrowsAsync<DomainException>(() => CreateSut().CreateAsync(Guid.Empty, CreateRequest()));

        _repository.Verify(r => r.AddAsync(It.IsAny<Product>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- Update -------------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_AppliesNamePriceAndStock_KeepsTheSku_AndPassesTheClientsRowVersionToTheStore()
    {
        var product = StoredProduct();
        StoreHolds(product);
        uint? expected = null;
        _repository.Setup(r => r.UpdateAsync(product, It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Callback<Product, uint, CancellationToken>((p, rowVersion, _) =>
            {
                expected = rowVersion;
                p.RowVersion = NextRowVersion;
                p.UpdatedAt = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc);
                p.UpdatedBy = Guid.NewGuid();
            })
            .Returns(Task.CompletedTask);

        var response = await CreateSut().UpdateAsync(product.Id, UpdateRequest());

        Assert.Equal(StoredRowVersion, expected);
        Assert.Equal(product.Id, response.Id);
        Assert.Equal("Widget Max", response.Name);
        Assert.Equal(20m, response.Price);
        Assert.Equal(7, response.Stock);
        Assert.Equal("WID-001", response.Sku);
        Assert.Equal(RowVersionToken.Encode(NextRowVersion), response.RowVersion);
        Assert.Equal(product.UpdatedAt, response.UpdatedAt);
        Assert.Equal(product.UpdatedBy, response.UpdatedBy);
        Assert.Equal(_callerTenantId, product.TenantId);
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02 })]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0x07, 0xD1 })] // an 8-byte token, e.g. one issued before the move to PostgreSQL
    public async Task UpdateAsync_WithATokenThatIsNotFourBytesLong_HandsTheStoreAVersionNoRowCanHold(byte[] token)
    {
        var product = StoredProduct();
        StoreHolds(product);
        uint? expected = null;
        _repository.Setup(r => r.UpdateAsync(product, It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Callback<Product, uint, CancellationToken>((_, rowVersion, _) => expected = rowVersion)
            .ThrowsAsync(new ProductConcurrencyException());
        var request = UpdateRequest();
        request.RowVersion = Convert.ToBase64String(token);

        await Assert.ThrowsAsync<ProductConcurrencyException>(() => CreateSut().UpdateAsync(product.Id, request));

        Assert.Equal(0u, expected); // xmin is never 0, so the store reports a stale write
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2001u)]
    [InlineData(uint.MaxValue)]
    public void RowVersionToken_RoundTripsAnyRowVersionAsFourBytes(uint rowVersion)
    {
        var token = RowVersionToken.Encode(rowVersion);

        Assert.True(StrictBase64.TryDecode(token, out var bytes));
        Assert.Equal(4, bytes.Length);
        Assert.Equal(rowVersion, RowVersionToken.ToRowVersion(bytes));
    }

    [Fact]
    public async Task UpdateAsync_ForAProductTheStoreDoesNotHold_ThrowsProductNotFoundExceptionAndWritesNothing()
    {
        var existing = StoredProduct();
        StoreHolds(existing);

        await Assert.ThrowsAsync<ProductNotFoundException>(() => CreateSut().UpdateAsync(Guid.NewGuid(), UpdateRequest()));

        _repository.Verify(r => r.UpdateAsync(It.IsAny<Product>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public static TheoryData<Exception> StoreRefusals => new()
    {
        new ProductConcurrencyException(),
        new ProductNotFoundException(),
    };

    [Theory]
    [MemberData(nameof(StoreRefusals))]
    public async Task UpdateAsync_WhenTheStoreRefusesTheWrite_SurfacesTheStoresException(Exception refusal)
    {
        var product = StoredProduct();
        StoreHolds(product);
        _repository.Setup(r => r.UpdateAsync(It.IsAny<Product>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(refusal);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => CreateSut().UpdateAsync(product.Id, UpdateRequest()));

        Assert.IsType(refusal.GetType(), thrown);
    }

    public static TheoryData<string, decimal, int> UpdatesTheDomainRejects => new()
    {
        { "", 1m, 1 },
        { "   ", 1m, 1 },
        { new string('n', 201), 1m, 1 },
        { "Widget", -0.01m, 1 },
        { "Widget", 1000000000m, 1 },
        { "Widget", 1.005m, 1 },
        { "Widget", 1m, -1 },
    };

    [Theory]
    [MemberData(nameof(UpdatesTheDomainRejects))]
    public async Task UpdateAsync_WithValuesTheDomainRejects_ThrowsDomainExceptionAndWritesNothing(string name, decimal price, int stock)
    {
        var product = StoredProduct();
        StoreHolds(product);

        await Assert.ThrowsAsync<DomainException>(() => CreateSut().UpdateAsync(product.Id, UpdateRequest(name, price, stock)));

        _repository.Verify(r => r.UpdateAsync(It.IsAny<Product>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("abc")]
    public async Task UpdateAsync_WithARowVersionThatIsNotBase64_ThrowsDomainExceptionAndWritesNothing(string rowVersion)
    {
        var product = StoredProduct();
        StoreHolds(product);
        var request = UpdateRequest();
        request.RowVersion = rowVersion;

        await Assert.ThrowsAsync<DomainException>(() => CreateSut().UpdateAsync(product.Id, request));

        _repository.Verify(r => r.UpdateAsync(It.IsAny<Product>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- Delete -------------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_ForAnExistingProduct_AsksTheStoreToDeleteExactlyThatId()
    {
        var id = Guid.NewGuid();
        _repository.Setup(r => r.DeleteAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await CreateSut().DeleteAsync(id);

        _repository.Verify(r => r.DeleteAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.DeleteAsync(It.Is<Guid>(other => other != id), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_WhenNothingWasDeleted_ThrowsProductNotFoundException()
    {
        var existing = Guid.NewGuid();
        _repository.Setup(r => r.DeleteAsync(existing, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _repository.Setup(r => r.DeleteAsync(It.Is<Guid>(id => id != existing), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateSut().DeleteAsync(existing);
        await Assert.ThrowsAsync<ProductNotFoundException>(() => CreateSut().DeleteAsync(Guid.NewGuid()));
    }
}
