using Conversia.Application.Assistant;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Assistant;

/// <summary>The administrator's counts of the assistant (docs/PLATFORM_ADMIN.md, "Assistant"): across companies, or for one. Counts only.</summary>
public sealed class AssistantMonitoringQueries(ApplicationDbContext db) : IAssistantMonitoringQueries
{
    private const int CommandTimeoutSeconds = 10;

    public async Task<IReadOnlyList<AssistantCountRow>> CountsAsync(Guid? tenantId, DateOnly fromDay, CancellationToken cancellationToken = default)
    {
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);
        var rows = await db.AssistantActivityCounts.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Day >= fromDay && (tenantId == null || c.TenantId == tenantId))
            .GroupBy(c => c.Metric)
            .Select(g => new { Metric = g.Key, Real = g.Where(c => !c.IsSandbox).Sum(c => c.Count), Sandbox = g.Where(c => c.IsSandbox).Sum(c => c.Count) })
            .ToListAsync(cancellationToken);
        return [.. rows.OrderBy(r => r.Metric, StringComparer.Ordinal).Select(r => new AssistantCountRow(r.Metric, r.Real, r.Sandbox))];
    }

    public async Task<(int Waiting, int Generating, int PendingReview)> BacklogAsync(Guid? tenantId, DateTime now, CancellationToken cancellationToken = default)
    {
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);
        var counts = await db.ConversationAssistants.IgnoreQueryFilters().AsNoTracking()
            .Where(a => tenantId == null || a.TenantId == tenantId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Waiting = g.Count(a => a.State == AssistantState.Waiting),
                Generating = g.Count(a => a.State == AssistantState.Waiting && a.ClaimedUntil > now),
                PendingReview = g.Count(a => a.State == AssistantState.HandedOff),
            })
            .FirstOrDefaultAsync(cancellationToken);
        return counts is null ? (0, 0, 0) : (counts.Waiting, counts.Generating, counts.PendingReview);
    }

    public async Task<(int CompaniesEnabled, bool CompanyEnabled, int Lines)> AdoptionAsync(Guid? tenantId, CancellationToken cancellationToken = default)
    {
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);
        var enabled = db.AssistantSettings.IgnoreQueryFilters().AsNoTracking().Where(s => s.Enabled);
        if (tenantId is null)
        {
            return (await enabled.CountAsync(cancellationToken), false, 0);
        }

        var companyEnabled = await enabled.AnyAsync(s => s.TenantId == tenantId, cancellationToken);
        var lines = await db.WhatsAppChannels.IgnoreQueryFilters().AsNoTracking().CountAsync(c => c.TenantId == tenantId && c.AssistantEnabled, cancellationToken);
        return (0, companyEnabled, lines);
    }
}
