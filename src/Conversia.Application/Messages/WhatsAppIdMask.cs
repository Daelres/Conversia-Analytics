namespace Conversia.Application.Messages;

/// <summary>Hides a customer's WhatsApp id in the places that only need to tell customers apart.</summary>
public static class WhatsAppIdMask
{
    private const string Ellipsis = "…";

    /// <summary>The last four digits behind an ellipsis ("…1234"): enough to tell two customers apart in a list, never the number.</summary>
    public static string Mask(string? waId)
    {
        var digits = new string((waId ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? Ellipsis : Ellipsis + (digits.Length <= 4 ? digits : digits[^4..]);
    }
}
