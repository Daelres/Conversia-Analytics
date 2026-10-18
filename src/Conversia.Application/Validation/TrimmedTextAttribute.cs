using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Conversia.Domain.Common;

namespace Conversia.Application.Validation;

/// <summary>
/// Free text applied to the trimmed value (the stored form): within <c>maxLength</c> and free of U+0000. Required by default;
/// with <see cref="Required"/> false a null or blank value is accepted. The rule is <see cref="TextRules"/>, the one the entities use.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class TrimmedTextAttribute : ValidationAttribute
{
    private readonly int _maxLength;

    public TrimmedTextAttribute(int maxLength)
    {
        _maxLength = maxLength;
    }

    public bool Required { get; set; } = true;

    public override bool IsValid(object? value)
        => value switch
        {
            null => !Required,
            string text => Required ? TextRules.IsValidRequired(text, _maxLength) : TextRules.IsValidOptional(text, _maxLength),
            _ => false,
        };

    public override string FormatErrorMessage(string name)
        => string.Format(CultureInfo.CurrentCulture, "El campo {0} {1}.", name, TextRules.RuleText(_maxLength, Required));
}
