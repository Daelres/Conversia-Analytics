using Conversia.Application.Classification.Llm;
using Conversia.Application.Monitoring;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Conversia.API.Configuration;

/// <summary>The health checks of <c>/health</c>, run on demand: names and statuses only (descriptions and exceptions stay out).</summary>
public sealed class HealthCheckServiceProbe(HealthCheckService health) : IPlatformHealthProbe
{
    public async Task<PlatformHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        var report = await health.CheckHealthAsync(cancellationToken);
        return new PlatformHealth(report.Status.ToString(), report.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value.Status.ToString())).ToList());
    }
}

/// <summary>
/// The provider and, with the LLM, the circuit breaker of THIS API instance (the breaker is process-local, design "Fallback and retry"): with
/// several instances each has its own, and the overview shows the one that answered. Its transitions are also logged (Warning).
/// </summary>
public sealed class ClassificationEngineStatus(ClassificationOptions options, IServiceProvider services) : IClassificationEngineStatus
{
    public string Provider => options.Provider;

    public string? BreakerState(DateTime now) => options.Llm is null ? null : services.GetService<LlmCircuitBreaker>()?.State(now).ToString();
}
