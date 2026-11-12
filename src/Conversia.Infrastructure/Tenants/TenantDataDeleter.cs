using Conversia.Application.Interfaces;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Tenants;

/// <summary><c>Tenants:Deletion:*</c>: the deletion job (docs/PLATFORM_ADMIN.md, "Deleting a company's data").</summary>
public sealed record TenantDeletionOptions(bool Enabled, int IntervalMinutes, int BatchSize)
{
    public const string SectionName = "Tenants:Deletion";

    public static TenantDeletionOptions Default { get; } = new(true, 60, 1000);
}

/// <summary>What one deletion run did for a company.</summary>
public enum TenantDeletionRun
{
    /// <summary>The deletion of the company has not started (or the company does not exist): nothing done.</summary>
    NotStarted,

    /// <summary>This run deleted the last rows and the company row.</summary>
    Completed,

    /// <summary>Another run (another instance) finished it first.</summary>
    CompletedElsewhere,

    /// <summary>Rows kept appearing after <see cref="TenantDataDeleter.MaxPasses"/> passes: the tombstone is marked stuck and the next run retries.</summary>
    Stuck,
}

/// <summary>
/// Deletes the data of cancelled companies (Ley 1581): those whose retention window passed, and those whose deletion already started (the
/// administrator asked for it, or a previous run stopped half way). Table by table in the order of <see cref="TenantDataDeletionPlan"/>,
/// in batches of <see cref="TenantDeletionOptions.BatchSize"/> rows, each in its own short transaction that also adds the batch to the
/// tombstone's counts: no long lock, nothing to redo after a crash (a resumed run starts again from the first table and finds the emptied
/// ones empty), exact counts, and two instances running at once serialize on the tombstone row. Last, in one transaction, the company row goes
/// and the tombstone and the audit entry say so. Raw SQL that names the company in every statement (the query filters do not apply to it).
/// </summary>
public sealed class TenantDataDeleter(IServiceScopeFactory scopeFactory, TenantDeletionOptions options, TimeProvider time, ILogger<TenantDataDeleter> logger)
{
    private static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Full passes over the plan in one run before giving up for this run (rows that keep arriving point at a defect).</summary>
    public const int MaxPasses = 5;

    /// <summary>Test hook: runs inside the finishing transaction right before its commit (to simulate a crash there).</summary>
    internal Func<CancellationToken, Task>? BeforeFinishCommit { get; set; }

    /// <summary>Test hook: runs at the start of the finishing step, before it checks that nothing is left (to simulate rows that keep arriving).</summary>
    internal Func<CancellationToken, Task>? BeforeFinishCheck { get; set; }

    /// <summary>Starts the due deletions and finishes every started one. Returns how many companies were deleted completely.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        List<Guid> due;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            due = await db.Tenants.AsNoTracking()
                .Where(t => t.Status == TenantStatus.Cancelled && (t.DataDeletionStartedAt != null || t.DataDeletionDueAt <= now))
                .OrderBy(t => t.DataDeletionStartedAt ?? t.DataDeletionDueAt)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
        }

        var completed = 0;
        foreach (var tenantId in due)
        {
            try
            {
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    // Starts it when the window passed (tombstone, audit); a no-op when it already started.
                    await scope.ServiceProvider.GetRequiredService<ITenantDeletionScheduler>().BeginScheduledDeletionAsync(tenantId, now, cancellationToken);
                }

                if (await DeleteAsync(tenantId, cancellationToken) == TenantDeletionRun.Completed)
                {
                    completed++;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Ids and the type only: the next run resumes where this one stopped.
                logger.LogError("The data deletion of tenant {TenantId} stopped and will resume at the next run ({ErrorType}).", tenantId, ex.GetType().Name);
            }
        }

        return completed;
    }

    /// <summary>Deletes everything of a company whose deletion started (see <see cref="TenantDeletionRun"/>).</summary>
    public async Task<TenantDeletionRun> DeleteAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (!await IsStartedAsync(tenantId, cancellationToken))
        {
            return TenantDeletionRun.NotStarted;
        }

        // Until a full pass finds every table empty (a delivery accepted just before the cancellation can arrive late), at most MaxPasses.
        for (var pass = 1; pass <= MaxPasses; pass++)
        {
            foreach (var step in TenantDataDeletionPlan.Steps)
            {
                while (await DeleteBatchAsync(tenantId, step, cancellationToken) == options.BatchSize)
                {
                }
            }

            var finished = await FinishAsync(tenantId, cancellationToken);
            if (finished is not null)
            {
                return finished.Value;
            }
        }

        await MarkStuckAsync(tenantId, cancellationToken);
        logger.LogError("The data deletion of tenant {TenantId} still found rows after {Passes} passes; it is marked stuck and will be tried again at the next run.", tenantId, MaxPasses);
        return TenantDeletionRun.Stuck;
    }

    private async Task<bool> IsStartedAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Cancelled && t.DataDeletionStartedAt != null, cancellationToken)
               && await db.TenantDeletions.AsNoTracking().AnyAsync(d => d.TenantId == tenantId && d.CompletedAt == null, cancellationToken);
    }

    /// <summary>One batch of one table, with its count added to the tombstone. Internal for the tests that simulate a run stopped half way.</summary>
    internal async Task<int> DeleteBatchAsync(Guid tenantId, TenantDataDeletionPlan.Step step, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await TransactionTimeouts.SetLocalAsync(db, StatementTimeout, StatementTimeout, cancellationToken);

        var tombstone = await LockTombstoneAsync(db, tenantId, cancellationToken);
        if (tombstone is null)
        {
            return 0;
        }

        // The table names are the plan's constants, never input; the company and the batch size are parameters.
#pragma warning disable EF1002 // the interpolated parts are the plan's constant table names
        var deleted = await db.Database.ExecuteSqlRawAsync(
            $"""DELETE FROM "{step.Table}" WHERE ctid = ANY(ARRAY({step.SelectSql}))""", [tenantId, options.BatchSize], cancellationToken);
#pragma warning restore EF1002
        tombstone.AddDeleted(step.Table, deleted);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    // The last step: only when nothing of the company is left does its row go (the RESTRICT foreign keys would refuse it anyway). Null: rows
    // are left, another pass is needed.
    private async Task<TenantDeletionRun?> FinishAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (BeforeFinishCheck is { } check)
        {
            await check(cancellationToken);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await TransactionTimeouts.SetLocalAsync(db, StatementTimeout, StatementTimeout, cancellationToken);

        var tombstone = await LockTombstoneAsync(db, tenantId, cancellationToken);
        if (tombstone is null)
        {
            return TenantDeletionRun.CompletedElsewhere;
        }

        foreach (var step in TenantDataDeletionPlan.Steps)
        {
#pragma warning disable EF1002 // the interpolated parts are the plan's constant table names
            var left = await db.Database.SqlQueryRaw<int>(
                $"""SELECT COUNT(*)::int AS "Value" FROM ({step.SelectSql}) rows""", tenantId, 1).SingleAsync(cancellationToken);
#pragma warning restore EF1002
            if (left > 0)
            {
                return null;
            }
        }

        var removed = await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "Tenants" WHERE "Id" = {tenantId} AND "Status" = 'Cancelled' AND "DataDeletionStartedAt" IS NOT NULL""", cancellationToken);
        tombstone.AddDeleted("Tenants", removed);
        tombstone.Complete(time.GetUtcNow().UtcDateTime);
        var nameHash = tombstone.NameHash;
        db.PlatformAuditLogs.Add(PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.TenantDataDeleted, tenantId, ReasonCodes.DeletionCompleted,
            new Dictionary<string, object?>
            {
                ["trigger"] = tombstone.Trigger.ToString(),
                ["nameHash"] = nameHash,
                ["hashKeyId"] = tombstone.HashKeyId,
                ["rows"] = tombstone.DeletedCounts(),
            }));
        await db.SaveChangesAsync(cancellationToken);
        if (BeforeFinishCommit is { } hook)
        {
            await hook(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("The data of tenant {TenantId} was deleted: {Rows} rows.", tenantId, tombstone.TotalDeleted);
        return TenantDeletionRun.Completed;
    }

    private async Task MarkStuckAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockTombstoneAsync(db, tenantId, cancellationToken) is { } tombstone)
        {
            tombstone.MarkStuck(time.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // The tombstone row is the deletion's lock: each batch and the finish take it first, so two instances never interleave on one company.
    private static async Task<TenantDeletion?> LockTombstoneAsync(ApplicationDbContext db, Guid tenantId, CancellationToken cancellationToken)
        => (await db.TenantDeletions
            .FromSql($"""SELECT * FROM "TenantDeletions" WHERE "TenantId" = {tenantId} AND "CompletedAt" IS NULL FOR UPDATE""")
            .ToListAsync(cancellationToken)).FirstOrDefault();
}
