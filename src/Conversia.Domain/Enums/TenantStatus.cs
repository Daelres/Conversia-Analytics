namespace Conversia.Domain.Enums;

/// <summary>
/// Where a client company stands with the platform. A company registers as <see cref="Pending"/> and cannot operate until a platform
/// administrator activates it. Stored by name.
/// </summary>
public enum TenantStatus
{
    /// <summary>Registered, waiting for the platform administrator. Its users can sign in but nothing else works.</summary>
    Pending = 0,

    /// <summary>Operating: its WhatsApp lines can be switched on, up to the quota of its commercial agreement.</summary>
    Active = 1,

    /// <summary>Stopped by the platform administrator. Nothing of it is processed until it is activated again.</summary>
    Suspended = 2,
}
