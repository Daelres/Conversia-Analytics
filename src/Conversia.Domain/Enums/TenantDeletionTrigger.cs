namespace Conversia.Domain.Enums;

/// <summary>What started the deletion of a cancelled company's data. Stored by name.</summary>
public enum TenantDeletionTrigger
{
    /// <summary>The retention window after the cancellation passed (the background job).</summary>
    Scheduled = 0,

    /// <summary>The platform administrator asked for it before the window ended.</summary>
    Manual = 1,
}
