using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Conversia.Application.Validation;

/// <summary>
/// The dates a query may name: from <see cref="Earliest"/> to today plus <see cref="MaxFutureDays"/>. Nothing in the product is older than the
/// first WhatsApp Business conversation, and a date far beyond today is a typo or an attack: without a bound, <c>from=9999-12-31</c> reaches the
/// date arithmetic (a period is 30 days long, an open end is derived from the other) and used to end as a 500. The check is on each date alone,
/// so it holds when only one end is given.
/// </summary>
public static class DateWindow
{
    public static readonly DateOnly Earliest = new(2015, 1, 1);

    public const int MaxFutureDays = 366;

    /// <summary>The latest date allowed at <paramref name="today"/>.</summary>
    public static DateOnly Latest(DateOnly today) => today.AddDays(MaxFutureDays);

    /// <summary>
    /// Null when <paramref name="value"/> is acceptable. "Today" is the UTC date of the request's <see cref="TimeProvider"/> (the one of the host when
    /// the validation context carries services, the system clock otherwise); a day of difference with the tenant's local date is irrelevant to a bound of a year.
    /// </summary>
    public static ValidationResult? Check(DateOnly? value, string displayName, string member, ValidationContext context)
    {
        if (value is not { } date)
        {
            return null;
        }

        var clock = context.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var latest = Latest(today);
        if (date >= Earliest && date <= latest)
        {
            return null;
        }

        return new ValidationResult(
            string.Create(CultureInfo.InvariantCulture, $"El campo {displayName} debe ser una fecha entre el {Earliest:yyyy-MM-dd} y el {latest:yyyy-MM-dd}."),
            [member]);
    }
}
