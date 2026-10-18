using System.ComponentModel.DataAnnotations;

namespace Conversia.Application.Validation;

/// <summary>Requires well-formed base64 text. Null is left to <see cref="RequiredAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class Base64Attribute : ValidationAttribute
{
    public Base64Attribute()
        : base(ValidationMessages.Base64)
    {
    }

    public override bool IsValid(object? value)
        => value is null || (value is string text && StrictBase64.TryDecode(text, out _));
}
