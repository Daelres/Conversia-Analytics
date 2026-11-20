using Conversia.Application.Classification.Llm;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.Reports;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Email;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Reports;

/// <summary>Counts and money of the month's reports per company, across companies (the administrator belongs to none).</summary>
public sealed class ReportMonitoringQueries(ApplicationDbContext db, AiQuotaSettings quota, EmailOptions email, TimeProvider time, bool aiConfigured) : IReportMonitoringQueries
{
    public async Task<ReportMonitoringResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var month = quota.MonthOf(now);
        var start = ReportAiAdmission.MonthStartUtc(now, quota);
        var rows = await (
                from r in db.ManagerReports.IgnoreQueryFilters().AsNoTracking()
                where r.GeneratedAt >= start
                group r by r.TenantId into g
                select new
                {
                    TenantId = g.Key,
                    Generated = g.Count(),
                    ByModel = g.Count(r => r.Generator == ReportGenerator.Llm),
                    Usd = db.ReportLedgerEntries.IgnoreQueryFilters().Where(e => e.TenantId == g.Key && e.CreatedAt >= start).Sum(e => (decimal?)e.EstimatedUsd) ?? 0m,
                })
            .ToListAsync(cancellationToken);
        var deliveries = await (
                from d in db.ReportDeliveries.IgnoreQueryFilters().AsNoTracking()
                join r in db.ManagerReports.IgnoreQueryFilters() on d.ReportId equals r.Id
                where r.GeneratedAt >= start
                group d by new { d.TenantId, d.Status } into g
                select new { g.Key.TenantId, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.TenantId).ToList();
        var names = await db.Tenants.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        int Count(Guid tenant, ReportDeliveryStatus status) => deliveries.Where(d => d.TenantId == tenant && d.Status == status).Sum(d => d.Count);

        var companies = rows
            .Select(r => new ReportMonitoringCompanyResponse(r.TenantId, names.GetValueOrDefault(r.TenantId, string.Empty), r.Generated, r.ByModel, r.Generated - r.ByModel,
                Count(r.TenantId, ReportDeliveryStatus.Sent), Count(r.TenantId, ReportDeliveryStatus.Failed), Count(r.TenantId, ReportDeliveryStatus.Pending), r.Usd))
            .OrderByDescending(r => r.Generated).ThenBy(r => r.TenantName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var totals = new ReportMonitoringCompanyResponse(Guid.Empty, string.Empty, companies.Sum(c => c.Generated), companies.Sum(c => c.ByModel), companies.Sum(c => c.ByTemplate),
            companies.Sum(c => c.Sent), companies.Sum(c => c.Failed), companies.Sum(c => c.Pending), companies.Sum(c => c.EstimatedUsd));
        return new ReportMonitoringResponse(month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), email.Provider, email.Enabled ? email.FromAddress : null, aiConfigured, totals, companies);
    }
}
