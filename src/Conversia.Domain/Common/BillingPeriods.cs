using System.Security.Cryptography;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Common;

/// <summary>
/// The calendar of the recurring charge. A period is a whole number of local dates (America/Bogota) <c>[start, end)</c>: monthly, on the
/// subscription's anchor day. A month without that day ends on its last day (an anchor of 31 gives 28 or 29 February, then 31 March).
/// </summary>
public static class BillingPeriods
{
    public const int MinAnchorDay = 1;
    public const int MaxAnchorDay = 31;

    /// <summary>The end (exclusive) of the monthly period that starts on <paramref name="start"/> with the anchor day <paramref name="anchorDay"/>.</summary>
    public static DateOnly EndOf(DateOnly start, int anchorDay)
    {
        if (anchorDay is < MinAnchorDay or > MaxAnchorDay)
        {
            throw new DomainException("El día de corte debe estar entre 1 y 31.");
        }

        var next = start.AddMonths(1);
        var day = Math.Min(anchorDay, DateTime.DaysInMonth(next.Year, next.Month));
        return new DateOnly(next.Year, next.Month, day);
    }
}

/// <summary>
/// The reference of a payment attempt, sent to the gateway and echoed in its events: unique (an index enforces it) and unguessable (128 random
/// bits), so a reference seen anywhere says nothing about the next one and cannot be forged to match one of ours.
/// </summary>
public static class BillingReference
{
    public const string Prefix = "cv_";

    /// <summary><see cref="Prefix"/> and 32 lowercase hexadecimal digits.</summary>
    public const int Length = 35;

    public static string New() => Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static bool IsValid(string? reference)
        => reference is { Length: Length } && reference.StartsWith(Prefix, StringComparison.Ordinal)
           && reference.AsSpan(Prefix.Length).ContainsAnyExcept("0123456789abcdef") is false;
}

/// <summary>Money of the charge: COP in cents, as the gateway wants it (<c>amount_in_cents</c>).</summary>
public static class BillingMoney
{
    public const string Currency = "COP";

    /// <summary>A price of a line or an add-on: at most 100 million pesos.</summary>
    public const long MaxPriceInCents = 100_000_000L * 100;

    public static readonly string PriceRuleMessage = $"El precio debe ser un valor en pesos entre 0 y {MaxPriceInCents / 100:N0}.";

    public static long Price(long cents)
        => cents is >= 0 and <= MaxPriceInCents && cents % 100 == 0 ? cents : throw new DomainException(PriceRuleMessage);
}
