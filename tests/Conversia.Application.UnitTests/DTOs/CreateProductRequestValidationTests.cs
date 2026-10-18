using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Conversia.Application.DTOs.Products;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>
/// Validation of <see cref="CreateProductRequest"/> (body of <c>POST /api/products</c>) through DataAnnotations —
/// the mechanism that gives the API per-field 400 errors. Every accepted case has a rejected twin so a request
/// type without validation cannot satisfy them.
/// </summary>
public class CreateProductRequestValidationTests
{
    private const string ValidName = "Widget Pro";
    private const string ValidSku = "WID-001";

    private static List<ValidationResult> ErrorsFor(string member, string? name = ValidName, string? sku = ValidSku, decimal price = 9.99m, int stock = 3)
    {
        var request = new CreateProductRequest { Name = name!, Sku = sku!, Price = price, Stock = stock };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results.Where(r => r.MemberNames.Contains(member)).ToList();
    }

    private static List<ValidationResult> AllErrors(string? name)
    {
        var request = new CreateProductRequest { Name = name!, Sku = ValidSku, Price = 9.99m, Stock = 3 };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void ValidRequest_PassesWhileEachFieldBroken_IsRejectedOnThatFieldOnly()
    {
        foreach (var member in new[] { "Name", "Sku", "Price", "Stock" })
        {
            Assert.Empty(ErrorsFor(member));
        }

        Assert.NotEmpty(ErrorsFor("Name", name: ""));
        Assert.NotEmpty(ErrorsFor("Sku", sku: ""));
        Assert.NotEmpty(ErrorsFor("Price", price: -1m));
        Assert.NotEmpty(ErrorsFor("Stock", stock: -1));

        Assert.Empty(ErrorsFor("Sku", name: ""));   // a broken name does not blame the SKU
        Assert.Empty(ErrorsFor("Name", stock: -1)); // nor does a broken stock blame the name
    }

    // ---- Name ---------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Name_EmptyOrWhitespace_IsRejected(string? name)
    {
        Assert.NotEmpty(ErrorsFor("Name", name: name));
    }

    [Fact]
    public void Name_AcceptsTwoHundredCharactersAndRejectsTwoHundredAndOne()
    {
        Assert.Empty(ErrorsFor("Name", name: new string('n', 200)));
        Assert.NotEmpty(ErrorsFor("Name", name: new string('n', 201)));
    }

    [Fact]
    public void Name_PaddedToMoreThanTwoHundredCharactersButTwoHundredAfterTrimming_IsAccepted()
    {
        // The limit applies to the trimmed value (what is stored), so the padding must not count against it.
        Assert.Empty(ErrorsFor("Name", name: $"  {new string('n', 200)}\t"));
        Assert.Empty(AllErrors(name: $"  {new string('n', 200)}\t"));
    }

    [Fact]
    public void Name_PaddedAndTwoHundredAndOneAfterTrimming_IsRejected()
    {
        Assert.NotEmpty(ErrorsFor("Name", name: $"  {new string('n', 201)}\t"));
    }

    // ---- SKU ----------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sku_EmptyOrWhitespace_IsRejected(string? sku)
    {
        Assert.NotEmpty(ErrorsFor("Sku", sku: sku));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("abc-123_X")]
    public void Sku_WithLettersDigitsDashAndUnderscore_IsAccepted(string sku)
    {
        Assert.Empty(ErrorsFor("Sku", sku: sku));
    }

    [Theory]
    [InlineData("AB CD")]
    [InlineData("AB.CD")]
    [InlineData("AB/CD")]
    [InlineData("AB%CD")]
    [InlineData("AB'CD")]
    [InlineData("AB;DROP TABLE")]
    [InlineData("AB😀")]
    public void Sku_WithOtherCharacters_IsRejected(string sku)
    {
        Assert.NotEmpty(ErrorsFor("Sku", sku: sku));
    }

    [Theory]
    [InlineData("  WID-001  ")]
    [InlineData("\tWID-001")]
    public void Sku_WithSurroundingWhitespace_IsNotAnInputError_BecauseItIsTrimmedAndThenCheckedForDuplicates(string sku)
    {
        // The API answers a padded duplicate with 409, so the boundary must let padded values through.
        Assert.Empty(ErrorsFor("Sku", sku: sku));
        Assert.NotEmpty(ErrorsFor("Sku", sku: sku.Trim() + " X")); // an inner space is still rejected
    }

    [Fact]
    public void Sku_AcceptsFiftyCharactersAndRejectsFiftyOne()
    {
        Assert.Empty(ErrorsFor("Sku", sku: new string('S', 50)));
        Assert.NotEmpty(ErrorsFor("Sku", sku: new string('S', 51)));
    }

    // ---- Price --------------------------------------------------------------------------

    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("12.5")]
    [InlineData("19.99")]
    [InlineData("999999999.99")]
    public void Price_InRangeWithAtMostTwoDecimals_IsAccepted(string price)
    {
        Assert.Empty(ErrorsFor("Price", price: D(price)));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("-1")]
    [InlineData("1000000000")]
    [InlineData("999999999.991")]
    [InlineData("0.001")]
    [InlineData("1.005")]
    [InlineData("79228162514264337593543950335")]
    public void Price_NegativeTooLargeOrWithMoreThanTwoDecimals_IsRejected(string price)
    {
        Assert.NotEmpty(ErrorsFor("Price", price: D(price)));
    }

    // ---- Stock --------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Stock_NonNegative_IsAccepted(int stock)
    {
        Assert.Empty(ErrorsFor("Stock", stock: stock));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Stock_Negative_IsRejected(int stock)
    {
        Assert.NotEmpty(ErrorsFor("Stock", stock: stock));
    }
}
