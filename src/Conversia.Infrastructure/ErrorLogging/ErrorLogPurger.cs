using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.ErrorLogging;

public sealed class ErrorLogPurger(ApplicationDbContext db) : IErrorLogPurger
{
    public const int BatchSize = 1000;

    // Batches keep each DELETE (and its locks and log) small; the OccurredAt index serves both the filter and the order.
    public async Task<int> PurgeAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default)
    {
        // Npgsql refuses to send a DateTime of Unspecified kind to a timestamptz column; the parameter is UTC by contract.
        olderThanUtc = olderThanUtc.Kind == DateTimeKind.Local ? olderThanUtc.ToUniversalTime() : DateTime.SpecifyKind(olderThanUtc, DateTimeKind.Utc);

        var total = 0;
        int deleted;
        do
        {
            var batch = db.ErrorLogs
                .Where(e => e.OccurredAt < olderThanUtc)
                .OrderBy(e => e.OccurredAt)
                .Take(BatchSize)
                .Select(e => e.Id);

            deleted = await db.ErrorLogs.Where(e => batch.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        return total;
    }
}
