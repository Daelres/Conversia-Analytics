using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Products;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>Validation of <see cref="ProductQuery"/> (query string of <c>GET /api/products</c>): page &gt;= 1, page size 1..100.</summary>
public class ProductQueryValidationTests
{
    private static List<ValidationResult> ErrorsFor(string member, int page = 1, int pageSize = 20)
    {
        var query = new ProductQuery { Page = page, PageSize = pageSize };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(query, new ValidationContext(query), results, validateAllProperties: true);
        return results.Where(r => r.MemberNames.Contains(member)).ToList();
    }

    [Fact]
    public void Defaults_ArePageOneWithTwentyItemsAndNoSearch_AndAreValid()
    {
        var query = new ProductQuery();

        Assert.Equal(1, query.Page);
        Assert.Equal(20, query.PageSize);
        Assert.Null(query.Search);
        Assert.Empty(ErrorsFor("Page", query.Page, query.PageSize));
        Assert.Empty(ErrorsFor("PageSize", query.Page, query.PageSize));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(int.MinValue, false)]
    public void Page_MustBeAtLeastOne(int page, bool accepted)
    {
        Assert.Equal(accepted, ErrorsFor("Page", page: page).Count == 0);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    [InlineData(-1, false)]
    [InlineData(int.MaxValue, false)]
    public void PageSize_MustBeBetweenOneAndOneHundred(int pageSize, bool accepted)
    {
        Assert.Equal(accepted, ErrorsFor("PageSize", pageSize: pageSize).Count == 0);
    }
}
