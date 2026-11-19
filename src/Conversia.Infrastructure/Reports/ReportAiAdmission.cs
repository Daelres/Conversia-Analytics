using Conversia.Application.Classification.Llm;
using Conversia.Application.Reports;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Reports;

/// <summary>
/// May the model write a company's next report (docs/REPORTS.md, "Cost and quota")? A category of AI use apart from the conversation analysis:
/// it never consumes the company's conversation quota and is bounded by its own limits, read from the stored reports of the month (America/Bogota):
/// the company's AI switch (the administrator's, shared with the analysis), the sandbox-only gate of the real provider, the company's model-written
/// reports of the month, and the deployment's estimated report spend of the month.
/// </summary>
/// <param name="sandboxOnly">The real provider reaches only companies in sandbox mode (<c>Classification:Llm:AllowRealClientNonSandbox</c> not set).</param>
public sealed class ReportAiAdmission(ApplicationDbContext db, ReportSettings settings, AiQuotaSettings quota, TimeProvider time, bool sandboxOnly) : IReportAiAdmission
{
    public async Task<ReportAiDecision> DecideAsync(Guid tenantId, bool sandboxMode, CancellationToken cancellationToken = default)
    {
        var enabled = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => (bool?)t.AiEnabled).FirstOrDefaultAsync(cancellationToken);
        if (enabled != true)
        {
            return ReportAiDecision.AiDisabled;
        }

        if (sandboxOnly && !sandboxMode)
        {
            return ReportAiDecision.SandboxOnly;
        }

        var monthStart = MonthStartUtc(time.GetUtcNow().UtcDateTime, quota);
        // The ledger counts every report written this month, previews and reports whose storing failed included.
        var written = db.ReportLedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(e => e.CreatedAt >= monthStart);
        if (await written.CountAsync(e => e.TenantId == tenantId && e.LlmAttempts > 0, cancellationToken) >= settings.MonthlyLlmReportsPerCompany)
        {
            return ReportAiDecision.MonthlyLimit;
        }

        var spent = await written.SumAsync(e => (decimal?)e.EstimatedUsd, cancellationToken) ?? 0m;
        return spent >= settings.DeploymentMonthlyUsdCap ? ReportAiDecision.DeploymentCap : ReportAiDecision.Allowed;
    }

    public static DateTime MonthStartUtc(DateTime utcNow, AiQuotaSettings quota) => ReportSchedule.ToUtc(quota.MonthOf(utcNow), 0, quota.TimeZone);
}
