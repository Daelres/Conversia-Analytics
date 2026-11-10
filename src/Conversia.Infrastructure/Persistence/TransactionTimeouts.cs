using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Persistence;

/// <summary>
/// Time limits of the current transaction, set with <c>set_config(..., is_local = true)</c> (that is <c>SET LOCAL</c> with a parameter):
/// they end with the transaction and never leak into the pooled connection. Call it right after the transaction begins.
/// </summary>
internal static class TransactionTimeouts
{
    /// <summary>
    /// Bounds every statement (<c>statement_timeout</c>) and the time the transaction may sit idle
    /// (<c>idle_in_transaction_session_timeout</c>); with <paramref name="lockTimeout"/>, also the wait for any lock (<c>lock_timeout</c>),
    /// advisory locks included.
    /// </summary>
    public static async Task SetLocalAsync(ApplicationDbContext db, TimeSpan timeout, TimeSpan? lockTimeout, CancellationToken cancellationToken)
    {
        var value = Milliseconds(timeout);
        await db.Database.ExecuteSqlAsync($"SELECT set_config('statement_timeout', {value}, true)", cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT set_config('idle_in_transaction_session_timeout', {value}, true)", cancellationToken);
        if (lockTimeout is { } wait)
        {
            var lockValue = Milliseconds(wait);
            await db.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', {lockValue}, true)", cancellationToken);
        }
    }

    private static string Milliseconds(TimeSpan timeout) => $"{Math.Max(1, (long)timeout.TotalMilliseconds)}ms";
}
