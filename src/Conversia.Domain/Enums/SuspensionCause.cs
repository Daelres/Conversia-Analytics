namespace Conversia.Domain.Enums;

/// <summary>Why a company is <see cref="TenantStatus.Suspended"/>. Stored by name.</summary>
public enum SuspensionCause
{
    /// <summary>The platform administrator stopped it: users blocked, webhooks refused, nothing processed.</summary>
    Manual = 0,

    /// <summary>A payment is late (set by the billing job): users see "payment pending", messages keep being received and stored.</summary>
    Billing = 1,
}
