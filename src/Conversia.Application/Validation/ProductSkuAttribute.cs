using System.ComponentModel.DataAnnotations;
using Conversia.Domain.Entities;

namespace Conversia.Application.Validation;

/// <summary>
/// SKU rule applied to the trimmed value (the stored form): 1-50 letters, digits, '-' or '_'.
/// Null is left to <see cref="RequiredAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ProductSkuAttribute : ValidationAttribute
{
    public ProductSkuAttribute()
        : base(ValidationMessages.ProductSku)
    {
    }

    public override bool IsValid(object? value)
        => value is null || (value is string sku && Product.IsValidSku(sku));
}
