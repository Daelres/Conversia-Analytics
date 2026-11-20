using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Repositories;

/// <summary>
/// The licence inventory. <c>LineLicenses</c> is a platform table (no tenant filter), and the company name is read from <c>Tenants</c> (never
/// filtered): nothing here switches a filter off. Matching a number reads the licence's digits, never the company's data.
/// </summary>
public sealed class LineLicenseRepository : ILineLicenseRepository
{
    private readonly ApplicationDbContext _db;

    public LineLicenseRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public Task<LineLicense?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => _db.LineLicenses.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

    public void Add(LineLicense license) => _db.LineLicenses.Add(license);

    public async Task SaveAsync(PlatformAuditLog? entry, CancellationToken cancellationToken = default)
    {
        if (entry is not null)
        {
            _db.PlatformAuditLogs.Add(entry);
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _db.ChangeTracker.Clear();
            throw new ConcurrencyConflictException(ex);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _db.ChangeTracker.Clear();
            throw new DuplicateLineLicenseException(ex);
        }
    }

    public async Task<LineLicense?> FindAssignedMatchAsync(Guid tenantId, string displayPhoneNumber, string? providerPhoneNumberId, CancellationToken cancellationToken = default)
    {
        var digits = LineLicense.DigitsOf(displayPhoneNumber);
        var providerId = string.IsNullOrWhiteSpace(providerPhoneNumberId) ? null : providerPhoneNumberId.Trim();
        return await _db.LineLicenses
            .Where(l => l.AssignedTenantId == tenantId && l.Status == LineLicenseStatus.Assigned && l.ChannelId == null
                && (l.PhoneDigits == digits || (providerId != null && l.ProviderChannelId == providerId)))
            .OrderBy(l => l.AssignedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<LineLicenseRow?> GetRowAsync(Guid id, CancellationToken cancellationToken = default)
        => Rows(_db.LineLicenses.AsNoTracking().Where(l => l.Id == id)).FirstOrDefaultAsync(cancellationToken);

    public async Task<ReadPage<LineLicenseRow>> ListAsync(LineLicenseFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var licenses = _db.LineLicenses.AsNoTracking();
        if (filter.Status is { } status)
        {
            licenses = licenses.Where(l => l.Status == status);
        }

        if (filter.TenantId is { } tenantId)
        {
            licenses = licenses.Where(l => l.AssignedTenantId == tenantId);
        }

        var total = await licenses.CountAsync(cancellationToken);
        var rows = await Rows(licenses.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(cancellationToken);
        return new ReadPage<LineLicenseRow>(rows, total);
    }

    // The digits leave this method only masked; the mask is applied after the projection (in memory), the rest is SQL.
    private IQueryable<LineLicenseRow> Rows(IQueryable<LineLicense> licenses)
        => licenses.Select(l => new LineLicenseRow(
            l.Id,
            l.Provider,
            LineLicense.Mask(l.PhoneDigits),
            l.ProviderChannelId,
            l.MonthlyCostUsd,
            l.CustomerOwned,
            l.Status,
            l.AssignedTenantId,
            _db.Tenants.Where(t => t.Id == l.AssignedTenantId).Select(t => t.Name).FirstOrDefault(),
            l.ChannelId,
            l.AssignedAt,
            l.ReleasedAt,
            l.RetiredAt,
            l.Notes,
            l.CreatedAt));
}
