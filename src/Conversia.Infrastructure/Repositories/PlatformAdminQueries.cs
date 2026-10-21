using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Repositories;

/// <summary>
/// The one place besides the webhook lookup and the workers where queries look across companies. Each <c>IgnoreQueryFilters()</c> below is
/// needed to answer a platform-level question (how many lines, who owns the company, when did it last receive a message, what is the
/// line's branch called) and projects scalars only: no message, contact, conversation or user record ever leaves this class.
/// </summary>
public class PlatformAdminQueries : IPlatformAdminQueries
{
    private readonly ApplicationDbContext _db;

    public PlatformAdminQueries(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<ReadPage<TenantAdminSummary>> ListTenantsAsync(TenantAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var tenants = _db.Tenants.AsNoTracking();
        if (filter.Status is { } status)
        {
            tenants = tenants.Where(t => t.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = "%" + EscapeLike(filter.Search.Trim()) + "%";
            tenants = tenants.Where(t => EF.Functions.ILike(t.Name, pattern, "\\"));
        }

        var total = await tenants.CountAsync(cancellationToken);
        var rows = await Summaries(tenants.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id).Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(cancellationToken);
        return new ReadPage<TenantAdminSummary>(rows, total);
    }

    public async Task<TenantAdminDetail?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var summary = await Summaries(_db.Tenants.AsNoTracking().Where(t => t.Id == tenantId)).FirstOrDefaultAsync(cancellationToken);
        if (summary is null)
        {
            return null;
        }

        var lines = await _db.WhatsAppChannels.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.DisplayPhoneNumber).ThenBy(c => c.Id)
            .Select(c => new TenantAdminLine(
                c.Id,
                c.DisplayPhoneNumber,
                _db.Branches.IgnoreQueryFilters().Where(b => b.Id == c.BranchId).Select(b => b.Name).FirstOrDefault(),
                c.IsActive))
            .ToListAsync(cancellationToken);
        return new TenantAdminDetail(summary, lines);
    }

    public async Task<ReadPage<PlatformAuditEntry>> ListAuditAsync(Guid? tenantId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var entries = _db.PlatformAuditLogs.AsNoTracking();
        if (tenantId is { } id)
        {
            entries = entries.Where(e => e.TenantId == id);
        }

        var total = await entries.CountAsync(cancellationToken);
        var rows = await entries
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new PlatformAuditEntry(
                e.Id,
                e.CreatedAt,
                e.AdminId,
                _db.PlatformAdmins.Where(a => a.Id == e.AdminId).Select(a => a.Email).FirstOrDefault(),
                e.Action,
                e.TenantId,
                e.Details))
            .ToListAsync(cancellationToken);
        return new ReadPage<PlatformAuditEntry>(rows, total);
    }

    private IQueryable<TenantAdminSummary> Summaries(IQueryable<Domain.Entities.Tenant> tenants)
        => tenants.Select(t => new TenantAdminSummary(
            t.Id,
            t.Name,
            t.Status,
            t.MaxActiveLines,
            _db.WhatsAppChannels.IgnoreQueryFilters().Count(c => c.TenantId == t.Id && c.IsActive),
            _db.WhatsAppChannels.IgnoreQueryFilters().Count(c => c.TenantId == t.Id),
            _db.Users.IgnoreQueryFilters().Where(u => u.TenantId == t.Id && u.IsOwner).OrderBy(u => u.CreatedAt).Select(u => u.Email).FirstOrDefault(),
            t.CreatedAt,
            // The latest message time only, through the (TenantId, SentAt) index: nothing of the message itself.
            _db.Messages.IgnoreQueryFilters().Where(m => m.TenantId == t.Id).Max(m => (DateTime?)m.SentAt),
            t.StatusChangedAt,
            t.StatusReason));

    private static string EscapeLike(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
