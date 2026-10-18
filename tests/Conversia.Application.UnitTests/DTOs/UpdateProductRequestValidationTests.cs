using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Conversia.Application.DTOs.Products;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>
/// Validation of <see cref="UpdateProductRequest"/> (body of <c>PUT /api/products/{id}</c>): the same name, price
/// and stock rules as creation plus a required base64 row version. Accepted cases are paired with rejected twins.
/// </summary>
public class UpdateProductRequestValidationTests
{
    private static readonly string ValidRowVersion = Convert.ToBase64String(new byte[] { 0, 0, 0, 0, 0, 0, 0x07, 0xD1 });

    private static List<ValidationResult> ErrorsFor(string member, string? name = "Widget Pro", decimal price = 9.99m, int stock = 3)
        => Validate(new UpdateProductRequest { Name = name!, Price = price, Stock = stock, RowVersion = ValidRowVersion }, member);

    private static List<ValidationResult> Validate(UpdateProductRequest request, string member)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results.Where(r => r.MemberNames.Contains(member)).ToList();
    }

    private static List<ValidationResult> RowVersionErrors(string? rowVersion)
        => Validate(new UpdateProductRequest { Name = "Widget Pro", Price = 1m, Stock = 1, RowVersion = rowVersion! }, "RowVersion");

    private static List<ValidationResult> AllErrors(string? name)
    {
        var request = new UpdateProductRequest { Name = name!, Price = 9.99m, Stock = 3, RowVersion = ValidRowVersion };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void ValidRequest_PassesWhileEachFieldBroken_IsRejectedOnThatField()
    {
        foreach (var member in new[] { "Name", "Price", "Stock", "RowVersion" })
        {
            Assert.Empty(ErrorsFor(member));
        }

        Assert.NotEmpty(ErrorsFor("Name", name: ""));
        Assert.NotEmpty(ErrorsFor("Price", price: -1m));
        Assert.NotEmpty(ErrorsFor("Stock", stock: -1));
        Assert.NotEmpty(RowVersionErrors(""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
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

    [Theory]
    [InlineData("0")]
    [InlineData("999999999.99")]
    [InlineData("1.1")]
    public void Price_Valid_IsAccepted(string price)
    {
        Assert.Empty(ErrorsFor("Price", price: D(price)));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1000000000")]
    [InlineData("1.005")]
    public void Price_Invalid_IsRejected(string price)
    {
        Assert.NotEmpty(ErrorsFor("Price", price: D(price)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(-1, false)]
    public void Stock_MustBeNonNegative(int stock, bool accepted)
    {
        Assert.Equal(accepted, ErrorsFor("Stock", stock: stock).Count == 0);
    }

    // ---- RowVersion ---------------------------------------------------------------------

    [Theory]
    [InlineData("AAAAAAAAB9E=")]   // any well-formed base64 passes validation; a token of the wrong size is answered later as a stale write (409)
    [InlineData("AAAAAAAAAAA=")]
    [InlineData("AQID")]           // 3 bytes: well-formed base64 is the DTO's concern, a mismatch is a 409 later
    public void RowVersion_WellFormedBase64_IsAccepted(string rowVersion)
    {
        Assert.Empty(RowVersionErrors(rowVersion));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!")]
    [InlineData("abc")]            // length not a multiple of 4
    [InlineData("====")]
    [InlineData("AAAA AAAA")]      // inner whitespace
    [InlineData("AAAAAAAAB9E=%%")]
    public void RowVersion_MissingOrNotBase64_IsRejected(string? rowVersion)
    {
        Assert.NotEmpty(RowVersionErrors(rowVersion));
    }
}
