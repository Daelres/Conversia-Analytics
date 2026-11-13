using Conversia.Application.DTOs.Metrics;

namespace Conversia.Application.Interfaces;

/// <summary>The caller's company's AI status for the dashboard (docs/CLASSIFICATION.md, "When the AI reads").</summary>
public interface IAiAnalysisStatusQueries
{
    Task<AiAnalysisStatusResponse> GetAsync(CancellationToken cancellationToken = default);
}
