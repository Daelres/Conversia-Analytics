using Conversia.Domain.Entities;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

public class ProductTests
{
    private static readonly Guid ValidTenantId = Guid.NewGuid();
    private const string ValidName = "Widget Pro";
    private const string ValidSku = "WID-001";
    private const decimal MaxPrice = 999_999_999.99m;

    private static Product NewProduct(string name = ValidName, string sku = ValidSku, decimal price = 12.34m, int stock = 5)
        => Product.Create(ValidTenantId, name, sku, price, stock);

    // ---- Create: happy path -----------------------------------------------------------

    [Fact]
    public void Create_WithValidInput_ReturnsProductWithNonEmptyIdAndTheGivenValues()
    {
        var product = Product.Create(ValidTenantId, ValidName, ValidSku, 12.34m, 5);

        Assert.NotEqual(Guid.Empty, product.Id);
        Assert.Equal(ValidTenantId, product.TenantId);
        Assert.Equal(ValidName, product.Name);
        Assert.Equal(ValidSku, product.Sku);
        Assert.Equal(12.34m, product.Price);
        Assert.Equal(5, product.Stock);
    }

    [Fact]
    public void Create_TwiceWithTheSameInput_ProducesDistinctIds()
    {
        Assert.NotEqual(NewProduct().Id, NewProduct().Id);
    }

    // ---- Tenant -------------------------------------------------------------------------

    [Fact]
    public void Create_WithEmptyTenantId_ThrowsDomainException()
    {
        Assert.NotNull(NewProduct()); // the same input with a tenant is valid
        Assert.Throws<DomainException>(() => Product.Create(Guid.Empty, ValidName, ValidSku, 1m, 1));
    }

    // ---- Name ---------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   \t ")]
    [InlineData(null)]
    public void Create_WithEmptyOrWhitespaceName_ThrowsDomainException(string? name)
    {
        Assert.Throws<DomainException>(() => NewProduct(name: name!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public void Create_WithNameOfAcceptedLength_Succeeds(int length)
    {
        var name = new string('n', length);

        Assert.Equal(name, NewProduct(name: name).Name);
    }

    [Fact]
    public void Create_WithNameOneCharacterOverTheMaximum_ThrowsDomainException()
    {
        Assert.Equal(200, NewProduct(name: new string('n', 200)).Name.Length);
        Assert.Throws<DomainException>(() => NewProduct(name: new string('n', 201)));
    }

    [Fact]
    public void Create_WithSurroundingWhitespaceInName_KeepsTheNameReadable()
    {
        // Whether the name is stored trimmed is not specified; it must in any case still be "Widget" once trimmed.
        Assert.Equal("Widget", NewProduct(name: "  Widget  ").Name.Trim());
    }

    // ---- SKU ----------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void Create_WithEmptyOrWhitespaceSku_ThrowsDomainException(string? sku)
    {
        Assert.Throws<DomainException>(() => NewProduct(sku: sku!));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("abc123")]
    [InlineData("ABC-123")]
    [InlineData("a_b-C_9")]
    [InlineData("-")]
    [InlineData("_")]
    public void Create_WithSkuMadeOfLettersDigitsDashAndUnderscore_Succeeds(string sku)
    {
        Assert.Equal(sku, NewProduct(sku: sku).Sku);
    }

    [Theory]
    [InlineData("AB CD")]
    [InlineData("AB.CD")]
    [InlineData("AB/CD")]
    [InlineData("AB\\CD")]
    [InlineData("AB@CD")]
    [InlineData("AB%CD")]
    [InlineData("AB'CD")]
    [InlineData("AB;DROP")]
    [InlineData("AB*")]
    [InlineData("AB\tCD")]
    [InlineData("AB\nCD")]
    [InlineData("AB😀")]
    public void Create_WithSkuContainingOtherCharacters_ThrowsDomainException(string sku)
    {
        Assert.Throws<DomainException>(() => NewProduct(sku: sku));
    }

    [Fact]
    public void Create_WithSkuAtAndOneOverTheMaximumLength_AcceptsFiftyAndRejectsFiftyOne()
    {
        Assert.Equal(50, NewProduct(sku: new string('S', 50)).Sku.Length);
        Assert.Throws<DomainException>(() => NewProduct(sku: new string('S', 51)));
    }

    [Theory]
    [InlineData("  WID-001  ")]
    [InlineData("\tWID-001\n")]
    public void Create_StoresTheSkuTrimmed(string paddedSku)
    {
        var product = NewProduct(sku: paddedSku);

        Assert.Equal(ValidSku.Length, product.Sku.Length);
        Assert.Equal(ValidSku, product.Sku, ignoreCase: true);
    }

    // ---- Price --------------------------------------------------------------------------

    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("1")]
    [InlineData("1.5")]
    [InlineData("1.10")]
    [InlineData("19.99")]
    [InlineData("999999999.99")]
    [InlineData("999999999")]
    public void Create_WithPriceInRangeAndAtMostTwoDecimals_StoresIt(string price)
    {
        var value = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(value, NewProduct(price: value).Price);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("-1")]
    [InlineData("1000000000")]
    [InlineData("1000000000.00")]
    [InlineData("999999999.991")]
    [InlineData("0.001")]
    [InlineData("1.005")]
    [InlineData("10.999")]
    [InlineData("79228162514264337593543950335")] // decimal.MaxValue
    [InlineData("-79228162514264337593543950335")] // decimal.MinValue
    public void Create_WithPriceNegativeTooLargeOrMoreThanTwoDecimals_ThrowsDomainException(string price)
    {
        var value = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Throws<DomainException>(() => NewProduct(price: value));
    }

    // ---- Stock --------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Create_WithNonNegativeStock_StoresIt(int stock)
    {
        Assert.Equal(stock, NewProduct(stock: stock).Stock);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Create_WithNegativeStock_ThrowsDomainException(int stock)
    {
        Assert.Throws<DomainException>(() => NewProduct(stock: stock));
    }

    // ---- Update -------------------------------------------------------------------------

    [Fact]
    public void Update_ChangesNamePriceAndStock_AndNothingElse()
    {
        var product = NewProduct();
        var id = product.Id;

        product.Update("Widget Max", 99.5m, 42);

        Assert.Equal("Widget Max", product.Name);
        Assert.Equal(99.5m, product.Price);
        Assert.Equal(42, product.Stock);
        Assert.Equal(ValidSku, product.Sku);          // the SKU is immutable after creation
        Assert.Equal(ValidTenantId, product.TenantId); // and so is the tenant
        Assert.Equal(id, product.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Update_WithEmptyOrWhitespaceName_ThrowsDomainExceptionAndKeepsTheProductUnchanged(string? name)
    {
        var product = NewProduct();

        Assert.Throws<DomainException>(() => product.Update(name!, 1m, 1));

        AssertUnchanged(product);
    }

    [Fact]
    public void Update_WithNameAtAndOneOverTheMaximum_AcceptsTwoHundredAndRejectsTwoHundredOne()
    {
        var product = NewProduct();
        product.Update(new string('n', 200), 1m, 1);
        Assert.Equal(200, product.Name.Length);

        Assert.Throws<DomainException>(() => product.Update(new string('n', 201), 1m, 1));

        Assert.Equal(200, product.Name.Length);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1000000000")]
    [InlineData("999999999.991")]
    [InlineData("1.005")]
    public void Update_WithInvalidPrice_ThrowsDomainExceptionAndKeepsTheProductUnchanged(string price)
    {
        var product = NewProduct();

        Assert.Throws<DomainException>(() => product.Update("New name", decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), 9));

        AssertUnchanged(product);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("999999999.99")]
    [InlineData("0.01")]
    public void Update_WithPriceAtTheBoundaries_StoresIt(string price)
    {
        var product = NewProduct();
        var value = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);

        product.Update("New name", value, 9);

        Assert.Equal(value, product.Price);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Update_WithNegativeStock_ThrowsDomainExceptionAndKeepsTheProductUnchanged(int stock)
    {
        var product = NewProduct();

        Assert.Throws<DomainException>(() => product.Update("New name", 5m, stock));

        AssertUnchanged(product);
    }

    [Fact]
    public void Update_WithZeroStock_StoresIt()
    {
        var product = NewProduct();

        product.Update("New name", 5m, 0);

        Assert.Equal(0, product.Stock);
    }

    private static void AssertUnchanged(Product product)
    {
        Assert.Equal(ValidName, product.Name);
        Assert.Equal(12.34m, product.Price);
        Assert.Equal(5, product.Stock);
        Assert.Equal(ValidSku, product.Sku);
    }

    [Fact]
    public void IsValidName_RejectsANulCharacter_AndCreateAndUpdateThrow()
    {
        Assert.False(Product.IsValidName("Wid\0get"));
        Assert.Throws<DomainException>(() => Product.Create(Guid.NewGuid(), "Wid\0get", ValidSku, 1m, 1));
        var product = Product.Create(Guid.NewGuid(), "Widget", ValidSku, 1m, 1);
        Assert.Throws<DomainException>(() => product.Update("Wid\0get", 1m, 1));
    }
}
