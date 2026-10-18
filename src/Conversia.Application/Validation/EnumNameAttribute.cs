using System.ComponentModel.DataAnnotations;

namespace Conversia.Application.Validation;

/// <summary>
/// The text must be the name of a member of the enum (<c>UpstreamUnavailable</c>), not a number: a number in a string would
/// parse as a member and name nothing the reader of the payload recognises. Null and blank are left to <see cref="RequiredAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class EnumNameAttribute : ValidationAttribute
{
    private readonly Type _enumType;

    public EnumNameAttribute(Type enumType)
        : base(ValidationMessages.EnumValue)
    {
        _enumType = enumType;
    }

    public override bool IsValid(object? value)
        => value is null or string { Length: 0 } || (value is string blank && string.IsNullOrWhiteSpace(blank)) || (value is string name && Array.Exists(Enum.GetNames(_enumType), n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
}
