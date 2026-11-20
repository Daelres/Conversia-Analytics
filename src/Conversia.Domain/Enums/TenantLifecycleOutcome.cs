namespace Conversia.Domain.Enums;

/// <summary>What a lifecycle change asked by a background job (billing, deletion) did. Every such change is idempotent.</summary>
public enum TenantLifecycleOutcome
{
    /// <summary>The company moved, and the move was audited.</summary>
    Changed = 0,

    /// <summary>The company already was where the change would take it: nothing written.</summary>
    Unchanged = 1,

    /// <summary>The change does not apply to the company's status (for example a payment never lifts an administrator's suspension): nothing written.</summary>
    NotApplicable = 2,
}
