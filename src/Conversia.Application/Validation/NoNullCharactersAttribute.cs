using System.ComponentModel.DataAnnotations;

namespace Conversia.Application.Validation;

/// <summary>
/// Rejects text containing U+0000. PostgreSQL text cannot store it, so such a value would otherwise fail at the database as a 500.
/// Null is left to <see cref="RequiredAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class NoNullCharactersAttribute : ValidationAttribute
{
    public NoNullCharactersAttribute()
        : base(ValidationMessages.NullCharacter)
    {
    }

    public override bool IsValid(object? value)
        => value is not string text || !text.Contains('\0');
}
