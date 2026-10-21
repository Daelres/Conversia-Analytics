using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Conversia.Infrastructure.Repositories;

/// <summary>
/// Serialises every change that depends on a company's lines through the lock of its <c>Tenants</c> row (<c>SELECT ... FOR UPDATE</c> in a
/// transaction): switching a line on checks the quota while holding it, so N parallel activations can never exceed the quota. The lock
/// is per company, so it never holds up another company, and the only other writers of the row are the administrator's changes.
/// </summary>
public sealed class TenantUnitOfWork : ITenantUnitOfWork
{
    private readonly ApplicationDbContext _dbContext;

    public TenantUnitOfWork(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ITenantChange?> BeginAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var alreadyTracked = _dbContext.ChangeTracker.Entries<Tenant>().Any(e => e.Entity.Id == tenantId);
        var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var locked = await _dbContext.Tenants
                .FromSql($"""SELECT * FROM "Tenants" WHERE "Id" = {tenantId} FOR UPDATE""")
                .ToListAsync(cancellationToken);
            if (locked.Count == 0)
            {
                await transaction.DisposeAsync();
                return null;
            }

            var tenant = locked[0];
            if (alreadyTracked)
            {
                // EF keeps the values it already holds for a tracked row; what counts is what the lock just protected.
                await _dbContext.Entry(tenant).ReloadAsync(cancellationToken);
            }

            return new TenantChange(_dbContext, transaction, tenant);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private sealed class TenantChange : ITenantChange
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly IDbContextTransaction _transaction;
        private bool _committed;

        public TenantChange(ApplicationDbContext dbContext, IDbContextTransaction transaction, Tenant tenant)
        {
            _dbContext = dbContext;
            _transaction = transaction;
            Tenant = tenant;
        }

        public Tenant Tenant { get; }

        // The company is named explicitly in every query: the lock belongs to ONE company, whoever is calling (a user of it, or an administrator).
        public Task<int> CountActiveLinesAsync(CancellationToken cancellationToken = default)
            => _dbContext.WhatsAppChannels.IgnoreQueryFilters().CountAsync(c => c.TenantId == Tenant.Id && c.IsActive, cancellationToken);

        public Task<WhatsAppChannel?> GetChannelAsync(Guid channelId, CancellationToken cancellationToken = default)
            => _dbContext.WhatsAppChannels.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == channelId && c.TenantId == Tenant.Id, cancellationToken);

        public void Record(PlatformAuditLog entry) => _dbContext.PlatformAuditLogs.Add(entry);

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _transaction.CommitAsync(cancellationToken);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                // Rolled back: the entities this change modified must not be saved by a later SaveChanges of the same scope.
                _dbContext.ChangeTracker.Clear();
            }

            await _transaction.DisposeAsync();
        }
    }
}
