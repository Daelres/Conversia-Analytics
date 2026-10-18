using System.ComponentModel.DataAnnotations;

namespace Conversia.Application.Validation;

/// <summary>
/// Password policy for new credentials: 12–128 characters with at least one
/// uppercase letter, one lowercase letter and one digit. Null is left to
/// <see cref="RequiredAttribute"/>. The message never includes the value.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class StrongPasswordAttribute : ValidationAttribute
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    public StrongPasswordAttribute()
        : base(ValidationMessages.StrongPassword)
    {
    }

    public override bool IsValid(object? value)
    {
        if (value is null)
        {
            return true;
        }

        return value is string password && MeetsPolicy(password);
    }

    private static bool MeetsPolicy(string password) =>
        password.Length is >= MinLength and <= MaxLength
        && password.Any(char.IsUpper)
        && password.Any(char.IsLower)
        && password.Any(char.IsDigit);
}
