namespace Conversia.Domain.Enums;

/// <summary>Where a line licence of the platform's inventory stands. Stored by name.</summary>
public enum LineLicenseStatus
{
    /// <summary>In the inventory, not given to any company.</summary>
    Available = 0,

    /// <summary>Given to one company, which may switch the matching number on.</summary>
    Assigned = 1,

    /// <summary>Out of the inventory for good (cancelled with the provider). Kept as a record.</summary>
    Retired = 2,
}
