namespace Conversia.Application.Classification.Llm;

/// <summary>Whether a tenant may use the LLM for its next classification (design D12). Anything but <see cref="Allowed"/>: the rules decide.</summary>
public enum AiAdmission
{
    Allowed,
    AiDisabled,
    TenantExhausted,
    TenantUsdCeiling,
    DeploymentCapReached,
    SandboxUsdCeiling,
}

/// <summary>Where a company stands with its AI usage this month, as the platform administrator sees it.</summary>
public enum AiUsageState
{
    /// <summary>The AI is switched off for the company.</summary>
    Disabled,

    Ok,

    /// <summary>At least 80% of the quota or of the USD ceiling used.</summary>
    Warning,

    /// <summary>The quota or the ceiling is used up (or the quota is 0): the rules read everything until the month ends.</summary>
    Exhausted,
}

/// <summary>One call to the model, as the meter records it: ids, the sandbox flag, tokens and the estimated cost. Never text.</summary>
/// <param name="WonDisagreement">The model and the rules disagreed about a sale (the Won guard decided).</param>
public sealed record AiUsageRecord(Guid TenantId, Guid ConversationId, bool IsSandbox, LlmUsage Usage, decimal EstimatedUsd, bool WonDisagreement = false);

/// <summary>
/// Tenant admission to the LLM and the record of what it cost (design D12, D13, D18; docs/CLASSIFICATION.md, "Usage and quota").
/// The metered implementation lives in Infrastructure (<c>AiUsageMeter</c>); <see cref="NullAiUsageMeter"/> stays for unit tests and for
/// the Stage A integration tests that build the coordinator by hand.
/// </summary>
public interface IAiUsageMeter
{
    /// <summary>A cheap read, no lock, no write.</summary>
    Task<AiAdmission> CheckAdmissionAsync(Guid tenantId, bool isSandbox, CancellationToken cancellationToken = default);

    /// <summary>
    /// A usable answer: the conversation counts once per month (distinct), tokens and cost are added. Written in a scope and transaction of
    /// its own, so a rollback of the classification never loses it. Never throws (a failure is logged): the classification goes on.
    /// </summary>
    Task RecordSuccessAsync(AiUsageRecord record, CancellationToken cancellationToken = default);

    /// <summary>A failed call: tokens and cost are added (no quota is consumed). Same scope and failure rules as <see cref="RecordSuccessAsync"/>.</summary>
    Task RecordFailureAsync(AiUsageRecord record, CancellationToken cancellationToken = default);
}

/// <summary>Usage records the meter could not write (the call happened, its cost is only in the log), for the administrator's monitoring.</summary>
public interface IAiUsageLossCounter
{
    /// <summary>Since this process started.</summary>
    long LostRecords { get; }

    DateTime? LastLostAt { get; }
}

/// <summary>Admits every tenant and records nothing (unit tests, hand-built coordinators of the Stage A integration tests).</summary>
public sealed class NullAiUsageMeter : IAiUsageMeter
{
    public static NullAiUsageMeter Instance { get; } = new();

    public Task<AiAdmission> CheckAdmissionAsync(Guid tenantId, bool isSandbox, CancellationToken cancellationToken = default) => Task.FromResult(AiAdmission.Allowed);

    public Task RecordSuccessAsync(AiUsageRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordFailureAsync(AiUsageRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// The deployment's AI quota settings (<c>Classification:Quota:*</c>): the default monthly conversations, the company USD ceiling per quota unit,
/// the deployment's monthly USD cap and the time zone of the month.
/// </summary>
/// <param name="SandboxMonthlyUsdPerCompany">The most a company's sandbox conversations may spend a month (they are outside its billable quota).</param>
public sealed record AiQuotaSettings(
    int DefaultMonthlyConversations, decimal UsdPerQuotaUnit, decimal DeploymentMonthlyUsdCap, TimeZoneInfo TimeZone, decimal SandboxMonthlyUsdPerCompany = 2m)
{
    public const string BogotaTimeZoneId = "America/Bogota";

    public static AiQuotaSettings Default { get; } = new(500, 0.02m, 20m, BogotaTimeZone(), 2m);

    public int EffectiveQuota(int? tenantQuota) => tenantQuota ?? DefaultMonthlyConversations;

    public decimal CeilingUsd(int effectiveQuota) => UsdPerQuotaUnit * effectiveQuota;

    /// <summary>The first day of the month <paramref name="utc"/> falls in, in <see cref="TimeZone"/>.</summary>
    public DateOnly MonthOf(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);
        return new DateOnly(local.Year, local.Month, 1);
    }

    /// <summary>The day <paramref name="utc"/> falls on, in <see cref="TimeZone"/>.</summary>
    public DateOnly DayOf(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone));

    public static DateOnly PreviousMonth(DateOnly month) => month.AddMonths(-1);

    /// <summary>America/Bogota from the system's time zone data, or its fixed UTC-5 (no daylight saving) when the image has none.</summary>
    public static TimeZoneInfo BogotaTimeZone()
        => TimeZoneInfo.TryFindSystemTimeZoneById(BogotaTimeZoneId, out var zone)
            ? zone
            : TimeZoneInfo.CreateCustomTimeZone(BogotaTimeZoneId, TimeSpan.FromHours(-5), BogotaTimeZoneId, BogotaTimeZoneId);
}

/// <param name="TenantUsd">The company's billable (non-sandbox) estimated USD this month.</param>
/// <param name="DeploymentUsd">Every company's estimated USD this month, sandbox included (it is real money).</param>
/// <param name="SandboxUsd">The company's sandbox estimated USD this month.</param>
public sealed record AiAdmissionFacts(
    bool AiEnabled, bool IsSandbox, int EffectiveQuota, int ConversationsAnalyzed, decimal TenantUsd, decimal TenantCeilingUsd, decimal DeploymentUsd,
    decimal DeploymentCapUsd, decimal SandboxUsd = 0m, decimal SandboxCeilingUsd = decimal.MaxValue);

/// <summary>
/// The admission order (design D12, D18): the company's switch, then the deployment cap, then the company's own quota and USD ceiling. A
/// sandbox conversation is outside the company's billable quota and ceiling (its usage is recorded apart) but has its own per-company USD
/// bound (<see cref="AiQuotaSettings.SandboxMonthlyUsdPerCompany"/>), and never escapes the switch or the deployment cap. The breaker is the
/// coordinator's, after this.
/// </summary>
public static class AiAdmissionPolicy
{
    public static AiAdmission Decide(AiAdmissionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.AiEnabled)
        {
            return AiAdmission.AiDisabled;
        }

        if (facts.DeploymentUsd >= facts.DeploymentCapUsd)
        {
            return AiAdmission.DeploymentCapReached;
        }

        if (facts.IsSandbox)
        {
            // Outside the billable quota, but bounded per company: one company's tests cannot spend everyone's cap.
            return facts.SandboxUsd >= facts.SandboxCeilingUsd ? AiAdmission.SandboxUsdCeiling : AiAdmission.Allowed;
        }

        if (facts.EffectiveQuota <= 0 || facts.ConversationsAnalyzed >= facts.EffectiveQuota)
        {
            return AiAdmission.TenantExhausted;
        }

        return facts.TenantUsd >= facts.TenantCeilingUsd ? AiAdmission.TenantUsdCeiling : AiAdmission.Allowed;
    }
}

public static class AiUsageStates
{
    private const decimal WarningShare = 0.8m;

    public static AiUsageState Of(bool enabled, int used, int quota, decimal usd, decimal ceilingUsd)
    {
        if (!enabled)
        {
            return AiUsageState.Disabled;
        }

        if (quota <= 0 || used >= quota || usd >= ceilingUsd)
        {
            return AiUsageState.Exhausted;
        }

        return used >= quota * WarningShare || usd >= ceilingUsd * WarningShare ? AiUsageState.Warning : AiUsageState.Ok;
    }
}
