namespace Conversia.Domain.Enums;

/// <summary>
/// Where a client company stands with the platform (docs/PLATFORM_ADMIN.md, "Lifecycle of a company"). A company registers as
/// <see cref="Pending"/> and cannot operate until a platform administrator activates it. Stored by name.
/// </summary>
public enum TenantStatus
{
    /// <summary>Registered, waiting for the platform administrator. Its users can sign in but nothing else works.</summary>
    Pending = 0,

    /// <summary>Operating: its WhatsApp lines can be switched on, up to the quota of its commercial agreement.</summary>
    Active = 1,

    /// <summary>
    /// Stopped. Why is <see cref="SuspensionCause"/>: by the administrator (<see cref="SuspensionCause.Manual"/>: nothing of it is
    /// received or processed) or for a late payment (<see cref="SuspensionCause.Billing"/>: its messages keep being received and stored).
    /// </summary>
    Suspended = 2,

    /// <summary>An agreed temporary pause: its users cannot work, its messages keep being received and stored, nothing is analysed or sent.</summary>
    Paused = 3,

    /// <summary>The service ended: nothing is received, its lines are off and its data is deleted once the retention window passes.</summary>
    Cancelled = 4,
}
