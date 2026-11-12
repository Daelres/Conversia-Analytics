namespace Conversia.Application.Classification.Llm;

/// <summary>Whether a tenant may use the LLM for its next classification (design D12). Anything but <see cref="Allowed"/>: the rules decide.</summary>
public enum AiAdmission
{
    Allowed,
    AiDisabled,
    TenantExhausted,
    TenantUsdCeiling,
    DeploymentCapReached,
}

/// <summary>
/// Tenant admission to the LLM (design D12/D13). Stage A ships only <see cref="NullAiUsageMeter"/>; the metered implementation (quota,
/// <c>AiEnabled</c>, ceilings, recorded usage) arrives with design PR 4a-1 and will add the recording methods.
/// </summary>
public interface IAiUsageMeter
{
    /// <summary>A cheap read, no lock, no write.</summary>
    Task<AiAdmission> CheckAdmissionAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Stage A (Development only): admits every tenant and records nothing.</summary>
public sealed class NullAiUsageMeter : IAiUsageMeter
{
    public static NullAiUsageMeter Instance { get; } = new();

    public Task<AiAdmission> CheckAdmissionAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(AiAdmission.Allowed);
}
