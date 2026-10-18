using System.ComponentModel.DataAnnotations;
using Conversia.Domain.Entities;

namespace Conversia.Application.Validation;

/// <summary>Price rule: 0 up to 999,999,999.99 with at most two decimals.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ProductPriceAttribute : ValidationAttribute
{
    public ProductPriceAttribute()
        : base(ValidationMessages.ProductPrice)
    {
    }

    public override bool IsValid(object? value)
        => value is null || (value is decimal price && Product.IsValidPrice(price));
}
