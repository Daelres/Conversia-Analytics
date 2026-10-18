using System.ComponentModel.DataAnnotations;
using Conversia.Domain.Entities;

namespace Conversia.Application.Validation;

/// <summary>
/// Name rule applied to the trimmed value (the stored form): required, at most 200 characters.
/// A null value is rejected too, so no separate <see cref="RequiredAttribute"/> is needed.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ProductNameAttribute : ValidationAttribute
{
    public ProductNameAttribute()
        : base(ValidationMessages.ProductName)
    {
    }

    public override bool IsValid(object? value)
        => value is string name && Product.IsValidName(name);
}
