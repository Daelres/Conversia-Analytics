using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Conversia.Infrastructure.Repositories;

/// <summary>
/// Serialises every change to a company's subscription through the lock of its <c>BillingSubscriptions</c> row (<c>SELECT ... FOR UPDATE</c>,
/// or <c>FOR UPDATE SKIP LOCKED</c> for the job, so two instances never work on the same subscription and never wait for each other). Every
/// query names the company explicitly and ignores the tenant filter: the job and the events endpoint run without a company on their scope.
/// </summary>
public sealed class BillingUnitOfWork : IBillingUnitOfWork
{
    private readonly ApplicationDbContext _db;

    public BillingUnitOfWork(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IBillingChange?> BeginAsync(Guid tenantId, bool skipIfLocked = false, CancellationToken cancellationToken = default)
    {
        var alreadyTracked = _db.ChangeTracker.Entries<BillingSubscription>().Any(e => e.Entity.TenantId == tenantId);
        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var locked = await (skipIfLocked
                    ? _db.BillingSubscriptions.FromSql($"""SELECT *, xmin FROM "BillingSubscriptions" WHERE "TenantId" = {tenantId} ORDER BY ("Status" = 'Cancelled'), "CreatedAt" DESC LIMIT 1 FOR UPDATE SKIP LOCKED""")
                    : _db.BillingSubscriptions.FromSql($"""SELECT *, xmin FROM "BillingSubscriptions" WHERE "TenantId" = {tenantId} ORDER BY ("Status" = 'Cancelled'), "CreatedAt" DESC LIMIT 1 FOR UPDATE"""))
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken);
            if (locked.Count == 0)
            {
                await transaction.DisposeAsync();
                return null;
            }

            var subscription = locked[0];
            if (alreadyTracked)
            {
                await _db.Entry(subscription).ReloadAsync(cancellationToken);
            }

            return new Change(_db, transaction, subscription);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private sealed class Change : IBillingChange
    {
        private readonly ApplicationDbContext _db;
        private readonly IDbContextTransaction _transaction;
        private bool _committed;

        public Change(ApplicationDbContext db, IDbContextTransaction transaction, BillingSubscription subscription)
        {
            _db = db;
            _transaction = transaction;
            Subscription = subscription;
        }

        public BillingSubscription Subscription { get; }

        private Guid TenantId => Subscription.TenantId;

        private IQueryable<BillingCharge> Charges => _db.BillingCharges.IgnoreQueryFilters().Where(c => c.TenantId == TenantId && c.SubscriptionId == Subscription.Id);

        private IQueryable<BillingPaymentAttempt> Attempts => _db.BillingPaymentAttempts.IgnoreQueryFilters().Where(a => a.TenantId == TenantId);

        public Task<BillingTenantFacts?> GetTenantFactsAsync(CancellationToken cancellationToken = default)
            => _db.Tenants.AsNoTracking().Where(t => t.Id == TenantId)
                .Select(t => new BillingTenantFacts(t.MaxActiveLines, t.Status, t.SuspensionCause, t.BillingHoldSince))
                .FirstOrDefaultAsync(cancellationToken);

        public Task<BillingPlan?> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default)
            => _db.BillingPlans.FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

        public Task<BillingAddOn?> GetAddOnAsync(Guid addOnId, CancellationToken cancellationToken = default)
            => _db.BillingAddOns.FirstOrDefaultAsync(a => a.Id == addOnId, cancellationToken);

        public async Task<BillingCharge?> GetOpenChargeAsync(CancellationToken cancellationToken = default)
            => (await GetOpenChargesAsync(cancellationToken)).FirstOrDefault();

        // Read from the database, then filtered as the tracked rows stand now: a charge paid earlier in this change is no longer open.
        public async Task<IReadOnlyList<BillingCharge>> GetOpenChargesAsync(CancellationToken cancellationToken = default)
            => (await Charges.Where(c => c.Status == BillingChargeStatus.Open).OrderBy(c => c.PeriodStart).ToListAsync(cancellationToken))
                .Where(c => c.IsOpen)
                .ToList();

        // By company, not by subscription: an attempt of a previous (cancelled) subscription still finds its charge.
        public Task<BillingCharge?> GetChargeAsync(Guid chargeId, CancellationToken cancellationToken = default)
            => _db.BillingCharges.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == TenantId && c.Id == chargeId, cancellationToken);

        public Task<bool> HasChargeForPeriodAsync(DateOnly periodStart, CancellationToken cancellationToken = default)
            => Charges.AnyAsync(c => c.PeriodStart == periodStart, cancellationToken);

        public async Task<IReadOnlyList<BillingPaymentAttempt>> GetAttemptsAsync(Guid chargeId, CancellationToken cancellationToken = default)
            => await Attempts.Where(a => a.ChargeId == chargeId).OrderBy(a => a.AttemptedAt).ToListAsync(cancellationToken);

        public Task<BillingPaymentAttempt?> GetAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default)
            => Attempts.FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);

        public async Task<IReadOnlyList<BillingPaymentAttempt>> GetPendingAttemptsAsync(CancellationToken cancellationToken = default)
            => (await Attempts.Where(a => a.Status == PaymentAttemptStatus.Pending).OrderBy(a => a.AttemptedAt).ToListAsync(cancellationToken))
                .Where(a => a.IsPending)
                .ToList();

        public Task<BillingPaymentAttempt?> GetAttemptByTransactionAsync(string transactionId, CancellationToken cancellationToken = default)
            => Attempts.FirstOrDefaultAsync(a => a.GatewayTransactionId == transactionId, cancellationToken);

        public void Add(BillingCharge charge) => _db.BillingCharges.Add(charge);

        public void Add(BillingPaymentAttempt attempt) => _db.BillingPaymentAttempts.Add(attempt);

        public void Add(AiQuotaGrant grant) => _db.AiQuotaGrants.Add(grant);

        public void Add(BillingReviewItem item) => _db.BillingReviewItems.Add(item);

        public Task<bool> HasOpenReviewItemAsync(string transactionId, CancellationToken cancellationToken = default)
            => _db.BillingReviewItems.AnyAsync(r => r.TransactionId == transactionId && r.ReviewedAt == null, cancellationToken);

        public void Record(PlatformAuditLog entry) => _db.PlatformAuditLogs.Add(entry);

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                throw new ConcurrencyConflictException(ex);
            }

            await _transaction.CommitAsync(cancellationToken);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                _db.ChangeTracker.Clear();
            }

            await _transaction.DisposeAsync();
        }
    }
}

public sealed class BillingRepository : IBillingRepository
{
    private readonly ApplicationDbContext _db;

    public BillingRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<BillingPlan>> ListPlansAsync(CancellationToken cancellationToken = default)
        => await _db.BillingPlans.AsNoTracking().OrderBy(p => p.Code).ToListAsync(cancellationToken);

    public Task<BillingPlan?> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default)
        => _db.BillingPlans.FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

    public async Task<IReadOnlyList<BillingAddOn>> ListAddOnsAsync(CancellationToken cancellationToken = default)
        => await _db.BillingAddOns.AsNoTracking().OrderBy(a => a.Code).ToListAsync(cancellationToken);

    public Task<BillingAddOn?> GetAddOnAsync(Guid addOnId, CancellationToken cancellationToken = default)
        => _db.BillingAddOns.FirstOrDefaultAsync(a => a.Id == addOnId, cancellationToken);

    public Task<bool> TryAddPlanAsync(BillingPlan plan, PlatformAuditLog audit, CancellationToken cancellationToken = default)
        => TryAddAsync(plan, audit, cancellationToken);

    public Task<bool> TryAddAddOnAsync(BillingAddOn addOn, PlatformAuditLog audit, CancellationToken cancellationToken = default)
        => TryAddAsync(addOn, audit, cancellationToken);

    public Task<bool> TryAddSubscriptionAsync(BillingSubscription subscription, PlatformAuditLog audit, CancellationToken cancellationToken = default)
        => TryAddAsync(subscription, audit, cancellationToken);

    public async Task SaveCatalogueAsync(PlatformAuditLog audit, CancellationToken cancellationToken = default)
    {
        _db.PlatformAuditLogs.Add(audit);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> TryAddAsync(object entity, PlatformAuditLog audit, CancellationToken cancellationToken)
    {
        _db.Add(entity);
        _db.PlatformAuditLogs.Add(audit);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            _db.Entry(entity).State = EntityState.Detached;
            _db.Entry(audit).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<BillingAccountView?> GetAccountAsync(Guid tenantId, int maxCharges, CancellationToken cancellationToken = default)
    {
        // The live subscription, else the latest (a cancelled one stays as history).
        var subscription = await _db.BillingSubscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.Status == SubscriptionStatus.Cancelled).ThenByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (subscription is null)
        {
            return null;
        }

        var plan = await _db.BillingPlans.AsNoTracking().FirstAsync(p => p.Id == subscription.PlanId, cancellationToken);
        var addOn = subscription.AddOnId is { } addOnId ? await _db.BillingAddOns.AsNoTracking().FirstOrDefaultAsync(a => a.Id == addOnId, cancellationToken) : null;
        var lines = await _db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.MaxActiveLines).FirstOrDefaultAsync(cancellationToken);
        var charges = maxCharges <= 0
            ? []
            : await _db.BillingCharges.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.SubscriptionId == subscription.Id)
                .OrderByDescending(c => c.PeriodStart)
                .Take(maxCharges)
                .ToListAsync(cancellationToken);
        var ids = charges.Select(c => c.Id).ToList();
        var attempts = ids.Count == 0
            ? []
            : await _db.BillingPaymentAttempts.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.TenantId == tenantId && ids.Contains(a.ChargeId))
                .OrderByDescending(a => a.AttemptedAt)
                .ToListAsync(cancellationToken);
        return new BillingAccountView(subscription, plan, addOn, lines, charges, attempts);
    }

    public async Task<IReadOnlyList<Guid>> ListDueAsync(DateTime now, int max, CancellationToken cancellationToken = default)
        => await _db.BillingSubscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.NextRunAt != null && s.NextRunAt <= now)
            .OrderBy(s => s.NextRunAt)
            .Take(max)
            .Select(s => s.TenantId)
            .ToListAsync(cancellationToken);

    public async Task<(Guid TenantId, Guid AttemptId)?> FindAttemptByReferenceAsync(string reference, CancellationToken cancellationToken = default)
    {
        var found = await _db.BillingPaymentAttempts.IgnoreQueryFilters().AsNoTracking().Where(a => a.Reference == reference)
            .Select(a => new { a.TenantId, a.Id }).FirstOrDefaultAsync(cancellationToken);
        return found is null ? null : (found.TenantId, found.Id);
    }

    public async Task<(Guid TenantId, Guid AttemptId)?> FindAttemptByTransactionAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        var found = await _db.BillingPaymentAttempts.IgnoreQueryFilters().AsNoTracking().Where(a => a.GatewayTransactionId == transactionId)
            .Select(a => new { a.TenantId, a.Id }).FirstOrDefaultAsync(cancellationToken);
        return found is null ? null : (found.TenantId, found.Id);
    }

    public async Task<(Guid TenantId, Guid ChargeId)?> FindChargeByPaymentLinkAsync(string paymentLinkId, CancellationToken cancellationToken = default)
    {
        var found = await _db.BillingCharges.IgnoreQueryFilters().AsNoTracking().Where(c => c.PaymentLinkId == paymentLinkId)
            .Select(c => new { c.TenantId, c.Id }).FirstOrDefaultAsync(cancellationToken);
        return found is null ? null : (found.TenantId, found.Id);
    }

    public async Task AddReviewItemAsync(BillingReviewItem item, CancellationToken cancellationToken = default)
    {
        // One open item per transaction: a re-sent event or a second run never piles them up.
        if (item.TransactionId is { } transactionId && await _db.BillingReviewItems.AnyAsync(r => r.TransactionId == transactionId && r.ReviewedAt == null, cancellationToken))
        {
            return;
        }

        _db.BillingReviewItems.Add(item);
        _db.PlatformAuditLogs.Add(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.BillingPaymentNeedsReview, item.TenantId, ReasonCodes.BillingPaymentNeedsReview, new Dictionary<string, object?>
        {
            ["reviewItemId"] = item.Id,
            ["transactionId"] = item.TransactionId,
            ["amountInCents"] = item.AmountInCents,
            ["why"] = item.Reason,
        }));
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BillingReviewItem>> ListOpenReviewItemsAsync(Guid? tenantId, int max, CancellationToken cancellationToken = default)
        => await _db.BillingReviewItems.AsNoTracking()
            .Where(r => r.ReviewedAt == null && (tenantId == null || r.TenantId == tenantId))
            .OrderBy(r => r.FlaggedAt)
            .Take(max)
            .ToListAsync(cancellationToken);

    public Task<BillingReviewItem?> GetReviewItemAsync(Guid id, CancellationToken cancellationToken = default)
        => _db.BillingReviewItems.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListOrphanBillingSuspensionsAsync(int max, CancellationToken cancellationToken = default)
        => await _db.Tenants.AsNoTracking()
            .Where(t => (t.Status == TenantStatus.Suspended && t.SuspensionCause == SuspensionCause.Billing) || (t.Status == TenantStatus.Paused && t.BillingHoldSince != null))
            .Where(t => _db.BillingSubscriptions.IgnoreQueryFilters().Any(s => s.TenantId == t.Id)
                        && !_db.BillingSubscriptions.IgnoreQueryFilters().Any(s => s.TenantId == t.Id && s.Status != SubscriptionStatus.Cancelled))
            .Select(t => t.Id)
            .Take(max)
            .ToListAsync(cancellationToken);

    public async Task<bool> TryAddEventAsync(BillingEvent billingEvent, CancellationToken cancellationToken = default)
    {
        // ON CONFLICT DO NOTHING: a re-sent event (same body) stores nothing and is answered 200 all the same.
        var inserted = await _db.Database.ExecuteSqlAsync($"""
            INSERT INTO "BillingEvents" ("Id", "BodyHash", "EventType", "TransactionId", "PaymentLinkId", "ReceivedAt", "Status", "Attempts", "NextAttemptAt", "CreatedAt")
            VALUES ({billingEvent.Id}, {billingEvent.BodyHash}, {billingEvent.EventType}, {billingEvent.TransactionId}, {billingEvent.PaymentLinkId},
                    {billingEvent.ReceivedAt}, {billingEvent.Status.ToString()}, 0, {billingEvent.NextAttemptAt}, {DatabaseTime.Truncate(DateTime.UtcNow)})
            ON CONFLICT ("BodyHash") DO NOTHING
            """, cancellationToken);
        return inserted == 1;
    }
}
