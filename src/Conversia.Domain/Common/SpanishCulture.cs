using System.Globalization;

namespace Conversia.Domain.Common;

/// <summary>
/// The one culture of the product: every text shown to users is Spanish and numbers are written the Spanish way
/// (<c>1.234,50</c>). The separators are set explicitly so the output does not depend on the ICU data of the host
/// (CLDR omits the group separator of four-digit numbers) and the culture still exists when globalization is invariant.
/// </summary>
public static class SpanishCulture
{
    public static CultureInfo Info { get; } = Create();

    public static string FormatAmount(decimal amount) => amount.ToString("N2", Info);

    public static string FormatWhole(int number) => number.ToString("N0", Info);

    /// <summary>"un decimal", "dos decimales": small counts are written in words, so a minimum of one never reads "1 decimales".</summary>
    public static string Decimals(int count) => count switch
    {
        1 => "un decimal",
        2 => "dos decimales",
        3 => "tres decimales",
        _ => $"{count} decimales"
    };

    /// <summary>The value the way a text box is filled: a comma as decimal mark, no group separator (<c>19,99</c>).</summary>
    public static string FormatInput(decimal amount) => amount.ToString("0.00", Info);

    private static CultureInfo Create()
    {
        CultureInfo culture;
        try
        {
            culture = new CultureInfo("es");
        }
        catch (CultureNotFoundException)
        {
            culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        }

        var number = (NumberFormatInfo)culture.NumberFormat.Clone();
        number.NumberDecimalSeparator = ",";
        number.NumberGroupSeparator = ".";
        number.NumberGroupSizes = [3];
        culture.NumberFormat = number;
        return CultureInfo.ReadOnly(culture);
    }
}
