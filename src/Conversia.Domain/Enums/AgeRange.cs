namespace Conversia.Domain.Enums;

/// <summary>
/// The coarse age range of a customer, only when the customer stated an age or a clear age-bearing fact. Never an exact age or a birth
/// date (data minimization). Null on the contact means "not stated". Stored by name; the wire codes are <c>under_18</c> ... <c>55_plus</c>.
/// <see cref="Under18"/> is allowed by decision of the business (purpose: know who contacts it); extracting the range is off by default.
/// </summary>
public enum AgeRange
{
    Under18 = 0,
    From18To24 = 1,
    From25To34 = 2,
    From35To44 = 3,
    From45To54 = 4,
    From55 = 5,
}
