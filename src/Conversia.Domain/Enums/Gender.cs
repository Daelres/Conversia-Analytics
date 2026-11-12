namespace Conversia.Domain.Enums;

/// <summary>The gender of a customer, only when the customer stated it; never inferred from a name or a style. Stored by name.</summary>
public enum Gender
{
    Undetermined = 0,
    Female = 1,
    Male = 2,
    Other = 3,
}
