using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Persistence;

/// <summary>
/// The per-tenant advisory lock that serializes everything that adds messages to a tenant's conversations: the webhook worker
/// (which only tries it) and a person sending a message (who waits for it). It is a transaction-level lock: taken inside a
/// transaction and released when that transaction ends. One definition of the key, so both sides always mean the same lock.
/// See docs/WEBHOOKS.md, "Lock order".
/// </summary>
internal static class TenantIngestionLock
{
    /// <summary>Takes the lock if it is free; returns false at once when another transaction holds it.</summary>
    public static Task<bool> TryAcquireAsync(ApplicationDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var key = tenantId.ToString();
        return db.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({key}, 0)) AS \"Value\"")
            .SingleAsync(cancellationToken);
    }

    /// <summary>Waits for the lock, for as long as the transaction's <c>lock_timeout</c> and <c>statement_timeout</c> allow (see <see cref="TransactionTimeouts"/>).</summary>
    public static Task AcquireAsync(ApplicationDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var key = tenantId.ToString();
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }
}
