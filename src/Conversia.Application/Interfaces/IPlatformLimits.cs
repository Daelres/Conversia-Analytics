using Conversia.Application.Classification.Llm;

namespace Conversia.Application.Interfaces;

/// <summary>
/// The per-company override of <c>Messaging:TenantSendRateLimitPerMinute</c>, as the send limiter reads it on every send (synchronously, from
/// memory). The admin service updates this instance at once; every instance reloads the overrides from the database periodically
/// (docs/PLATFORM_ADMIN.md, "Limits").
/// </summary>
public interface ITenantSendRateLimits
{
    /// <summary>The company's own limit, or null for the deployment default.</summary>
    int? OverrideFor(Guid tenantId);

    void Update(Guid tenantId, int? perMinute);
}

/// <summary>The deployment defaults a company's own limits fall back to.</summary>
/// <param name="MaxSendRateLimitPerMinute">The most a company's own send limit may be (<c>Messaging:TenantSendRateLimitMaxPerMinute</c>).</param>
public sealed record PlatformLimitDefaults(
    int TenantSendRateLimitPerMinute, AiQuotaSettings Ai, int MaxSendRateLimitPerMinute = 1200, Classification.Scheduling.AnalysisScheduleSettings? Schedule = null)
{
    /// <summary>The LLM schedule's deployment settings (<c>Classification:Schedule:*</c>).</summary>
    public Classification.Scheduling.AnalysisScheduleSettings ScheduleSettings => Schedule ?? Classification.Scheduling.AnalysisScheduleSettings.Default;
}

/// <summary>Counts how each manual send ended, per company and day, for the platform's monitoring. Never text, never numbers.</summary>
public interface IMessageSendOutcomeRecorder
{
    /// <summary>Never throws (a failure is logged): the answer to the person sending must not depend on the counter.</summary>
    Task RecordAsync(Guid tenantId, bool isSandbox, string outcome, CancellationToken cancellationToken = default);
}
