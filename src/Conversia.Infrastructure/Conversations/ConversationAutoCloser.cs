using Conversia.Domain.Enums;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Conversia.Infrastructure.Conversations;

/// <summary>
/// Closes the open conversations whose last message is older than <c>Conversations:AutoCloseAfterDays</c>, across all tenants (the job is an
/// operations rule), in bounded batches: each batch is one short transaction that locks its conversations (<c>FOR UPDATE SKIP LOCKED</c>:
/// a conversation that is receiving a message, or being decided by a person, is skipped and met again on the next run) and releases them
/// on commit. When the last message was the business's and the customer had written before (the customer went silent), the opportunity
/// becomes Lost for <see cref="LossReason.NoResponse"/>, unless it is already Won or Lost or a person decided it.
/// </summary>
public sealed class ConversationAutoCloser(ApplicationDbContext db, ConversationLifecycleOptions options)
{
    private const int StatementTimeoutSeconds = 30;

    /// <summary>Closes one batch and returns how many conversations it closed (0 when none is due).</summary>
    public async Task<ClosedBatch> CloseBatchAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var cutoff = utcNow - options.CloseAfter;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var timeout = $"{StatementTimeoutSeconds}s";
        await db.Database.ExecuteSqlAsync($"SELECT set_config('statement_timeout', {timeout}, true)", cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT set_config('idle_in_transaction_session_timeout', {timeout}, true)", cancellationToken);

        // Across all tenants on purpose. LIMIT is inside the locking query, so only the conversations of the batch are locked.
        var batch = await db.Conversations
            .FromSql($"""
                SELECT * FROM "Conversations"
                WHERE "Status" = 'Open' AND "LastMessageAt" < {cutoff}
                ORDER BY "LastMessageAt"
                LIMIT {options.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        if (batch.Count == 0)
        {
            return new ClosedBatch(0, 0);
        }

        var ids = batch.Select(c => c.Id).ToList();
        var opportunities = await db.Opportunities.IgnoreQueryFilters()
            .Where(o => ids.Contains(o.ConversationId))
            .ToDictionaryAsync(o => o.ConversationId, cancellationToken);

        // Who spoke last. Messages of the same second count for the customer: when it is unclear, a conversation is not given up for lost.
        var lastDirections = (await db.Messages.IgnoreQueryFilters().AsNoTracking()
                .Where(m => ids.Contains(m.ConversationId))
                .GroupBy(m => m.ConversationId)
                .Select(g => new
                {
                    ConversationId = g.Key,
                    Direction = g.OrderByDescending(m => m.SentAt).ThenBy(m => m.Direction).Select(m => m.Direction).First(),
                })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.ConversationId, x => x.Direction);

        var lost = 0;
        foreach (var conversation in batch)
        {
            conversation.Close(utcNow);

            if (conversation.FirstInboundAt is not null
                && lastDirections.TryGetValue(conversation.Id, out var last) && last == MessageDirection.Outbound
                && opportunities.TryGetValue(conversation.Id, out var opportunity)
                && opportunity.MarkLostForNoResponse(utcNow))
            {
                lost++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ClosedBatch(batch.Count, lost);
    }

    /// <param name="Closed">Conversations closed by the batch.</param>
    /// <param name="LostForNoResponse">Of those, the opportunities that became Lost for lack of an answer.</param>
    public sealed record ClosedBatch(int Closed, int LostForNoResponse);
}
