using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Reports;
using Conversia.Application.Reports;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Reports;

/// <summary>The manager reports' data (docs/REPORTS.md). Tenant-scoped by the query filters, except where <see cref="IReportStore"/> says otherwise.</summary>
public sealed class ReportStore(ApplicationDbContext db) : IReportStore
{
    /// <summary>Templates whose body has exactly these placeholders can carry a report: the period, the headline and the link.</summary>
    public const int TemplateParameters = 3;

    public async Task<IReadOnlyList<ReportDefinition>> ListDefinitionsAsync(CancellationToken cancellationToken = default)
        => await db.ReportDefinitions.AsNoTracking().Include(d => d.Recipients).ToListAsync(cancellationToken);

    public Task<ReportDefinition?> FindDefinitionAsync(Guid id, CancellationToken cancellationToken = default)
        => db.ReportDefinitions.Include(d => d.Recipients).FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<int> CountDefinitionsAsync(CancellationToken cancellationToken = default) => db.ReportDefinitions.CountAsync(cancellationToken);

    public void AddDefinition(ReportDefinition definition) => db.ReportDefinitions.Add(definition);

    public async Task DeleteDefinitionAsync(ReportDefinition definition, CancellationToken cancellationToken = default)
    {
        // The foreign keys cascade (recipients, reports, deliveries); deleting by statement avoids loading the history.
        await db.ReportDefinitions.Where(d => d.Id == definition.Id).ExecuteDeleteAsync(cancellationToken);
        db.Entry(definition).State = EntityState.Detached;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => db.SaveChangesAsync(cancellationToken);

    public async Task<IReadOnlyList<ReportUserFact>> ListUsersAsync(CancellationToken cancellationToken = default)
        => await db.Users.AsNoTracking()
            .Select(u => new ReportUserFact(u.Id, u.Email, u.ReportsWhatsAppNumber != null && u.ReportsWhatsAppOptInAt != null && u.ReportsWhatsAppVerifiedAt != null))
            .ToListAsync(cancellationToken);

    public Task<User?> FindUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public async Task<IReadOnlyList<ReportBranchOption>> ListBranchesAsync(CancellationToken cancellationToken = default)
        => await db.Branches.AsNoTracking().OrderBy(b => b.Name).Select(b => new ReportBranchOption(b.Id, b.Name)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> ListAdvisorNamesAsync(CancellationToken cancellationToken = default)
        => await db.Advisors.AsNoTracking().Select(a => a.Name).ToListAsync(cancellationToken);

    public Task<ReportTenantFact?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => new ReportTenantFact(t.Name, t.SandboxMode, t.AiEnabled)).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ReportTemplateOption>> ListTemplateOptionsAsync(CancellationToken cancellationToken = default)
        => await (
                from t in db.MessageTemplates.AsNoTracking()
                join c in db.WhatsAppChannels.AsNoTracking() on t.ChannelId equals c.Id
                where c.IsActive && t.Status == TemplateStatus.Approved && t.Category == TemplateCategory.Utility && t.IsSupported
                      && t.ParameterNames.Length == TemplateParameters
                orderby c.DisplayPhoneNumber, t.Name, t.Language
                select new ReportTemplateOption(c.Id, c.DisplayPhoneNumber, t.Name, t.Language, t.BodyText))
            .ToListAsync(cancellationToken);

    public async Task<PagedResponse<ReportSummaryResponse>> ListReportsAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var total = await db.ManagerReports.CountAsync(cancellationToken);
        var rows = await db.ManagerReports.AsNoTracking()
            .OrderByDescending(r => r.GeneratedAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new
            {
                Report = r,
                Sent = db.ReportDeliveries.Count(d => d.ReportId == r.Id && d.Status == ReportDeliveryStatus.Sent),
                Failed = db.ReportDeliveries.Count(d => d.ReportId == r.Id && d.Status == ReportDeliveryStatus.Failed),
                Pending = db.ReportDeliveries.Count(d => d.ReportId == r.Id && d.Status == ReportDeliveryStatus.Pending),
                Skipped = db.ReportDeliveries.Count(d => d.ReportId == r.Id && d.Status == ReportDeliveryStatus.Skipped),
            })
            .ToListAsync(cancellationToken);
        return new PagedResponse<ReportSummaryResponse>
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            Items = [.. rows.Select(x => new ReportSummaryResponse(
                x.Report.Id, x.Report.DefinitionId, x.Report.DefinitionName, x.Report.Frequency.ToString(),
                ReportService.Period(x.Report.Frequency, new ReportPeriod(x.Report.PeriodFrom, x.Report.PeriodTo)), x.Report.Generator.ToString(), x.Report.Headline,
                x.Report.IsSandbox, x.Report.GeneratedAt, new ReportDeliveryCounts(x.Sent, x.Failed, x.Pending, x.Skipped)))],
        };
    }

    public async Task<(ManagerReport Report, IReadOnlyList<ReportDeliveryRow> Deliveries)?> GetReportAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        var report = await db.ManagerReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
        if (report is null)
        {
            return null;
        }

        var deliveries = await (
                from d in db.ReportDeliveries.AsNoTracking()
                join u in db.Users.AsNoTracking() on d.UserId equals u.Id
                where d.ReportId == reportId
                orderby u.Email, d.Channel
                select new { Delivery = d, u.Email })
            .ToListAsync(cancellationToken);
        return (report, [.. deliveries.Select(d => new ReportDeliveryRow(d.Delivery, d.Email))]);
    }

    public void AddLedgerEntry(ReportLedgerEntry entry) => db.ReportLedgerEntries.Add(entry);

    public Task<bool> ModelAlreadyCalledAsync(Guid definitionId, ReportFrequency frequency, DateOnly periodFrom, CancellationToken cancellationToken = default)
        => db.ReportLedgerEntries.AnyAsync(e => e.DefinitionId == definitionId && e.Kind == ReportCompositionKind.Scheduled && e.Frequency == frequency
            && e.PeriodFrom == periodFrom && e.LlmAttempts > 0, cancellationToken);

    public Task<int> CountPreviewsSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken = default)
        => db.ReportLedgerEntries.CountAsync(e => e.Kind == ReportCompositionKind.Preview && e.CreatedAt >= sinceUtc, cancellationToken);

    public Task<bool> IsRecipientAsync(Guid definitionId, Guid userId, CancellationToken cancellationToken = default)
        => db.ReportRecipients.AnyAsync(r => r.DefinitionId == definitionId && r.UserId == userId, cancellationToken);

    public async Task<(ReportRecipient Recipient, string DefinitionName)?> FindRecipientForUnsubscribeAsync(Guid recipientId, CancellationToken cancellationToken = default)
    {
        var recipient = await db.ReportRecipients.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == recipientId, cancellationToken);
        if (recipient is null)
        {
            return null;
        }

        var name = await db.ReportDefinitions.IgnoreQueryFilters().Where(d => d.Id == recipient.DefinitionId).Select(d => d.Name).FirstAsync(cancellationToken);
        return (recipient, name);
    }
}
