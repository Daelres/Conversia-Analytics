using Conversia.Application.DTOs.Metrics;
using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Metrics;

/// <summary>The company's AI switch and the latest AI update of its conversations, under the normal tenant filters.</summary>
public sealed class AiAnalysisStatusQueries(ApplicationDbContext db, ICurrentTenantService tenant) : IAiAnalysisStatusQueries
{
    public async Task<AiAnalysisStatusResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var enabled = await db.Tenants.AsNoTracking().Where(t => t.Id == tenant.TenantId).Select(t => t.AiEnabled).FirstOrDefaultAsync(cancellationToken);
        var last = await db.Conversations.AsNoTracking().MaxAsync(c => c.LlmAnalyzedAt, cancellationToken);
        return new AiAnalysisStatusResponse { Enabled = enabled, LastUpdatedAt = last };
    }
}
