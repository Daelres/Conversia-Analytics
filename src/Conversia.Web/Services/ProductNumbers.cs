using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Conversia.Application.Validation;

namespace Conversia.Web.Services;

/// <summary>
/// Reads the price and stock the user typed. A price is plain ASCII digits with at most two decimals after one comma or one dot
/// ("19,99", "19.99", "1,5" is 1.5): a thousands separator, a sign, a third decimal, another script's digits or any other
/// text is refused rather than guessed ("1,234" could be 1.234 or 1234). Blank, missing and overflowing text is refused
/// instead of becoming 0.
/// </summary>
internal static partial class ProductNumbers
{
    [GeneratedRegex(@"^[0-9]+(?:[.,][0-9]{1,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex PricePattern();

    public static bool TryParsePrice(string? text, out decimal price)
    {
        price = 0;
        var trimmed = text?.Trim();
        return trimmed is not null
            && PricePattern().IsMatch(trimmed)
            && decimal.TryParse(trimmed.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out price);
    }

    public static bool TryParseStock(string? text, out int stock)
        => int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out stock);
}

/// <summary>Price text the API's price rule accepts (the rule itself stays in the Application project).</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class PriceTextAttribute : ValidationAttribute
{
    private static readonly ProductPriceAttribute Rule = new();

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (!ProductNumbers.TryParsePrice(value as string, out var price))
        {
            return new ValidationResult(UiMessages.PriceNotANumber, [validationContext.MemberName!]);
        }

        return Rule.IsValid(price)
            ? ValidationResult.Success
            : new ValidationResult(Rule.FormatErrorMessage(validationContext.DisplayName), [validationContext.MemberName!]);
    }
}

/// <summary>Stock text that is a whole number of 0 or more.</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class StockTextAttribute : ValidationAttribute
{
    public StockTextAttribute()
        : base(UiMessages.StockInvalid)
    {
    }

    public override bool IsValid(object? value)
        => ProductNumbers.TryParseStock(value as string, out var stock) && stock >= 0;
}
