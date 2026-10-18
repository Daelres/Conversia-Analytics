using Conversia.Application.Classification;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Classification;

/// <summary>
/// Classifies one waiting conversation at a time. Safe to run in several workers and several API instances at once. One bounded transaction per
/// conversation (<c>statement_timeout</c>, <c>idle_in_transaction_session_timeout</c> and a total budget):
/// <list type="number">
/// <item><b>Claim</b>: <c>SELECT ... FOR UPDATE SKIP LOCKED</c> takes the oldest conversation that is flagged, whose request has waited out the
/// debounce, has failed fewer than <c>MaxFailures</c> times and is not waiting out a retry delay. The debounce counts from when ingestion
/// <i>requested</i> the classification (<see cref="Conversation.ClassificationRequestedAt"/>, the arrival of the last message), not from the
/// time written on the message: a message the provider delivers late, or a replayed one, still gets its quiet period. A row another worker (or a person deciding the outcome,
/// or ingestion adding a message) holds is skipped, never waited for.</item>
/// <item><b>Read and write</b>: the lock of the conversation row is the lock of "the conversation and its opportunity", so nothing else
/// changes either while its messages are read and the opportunity is written. The tenant is set on the scope first, and everything after the claim runs
/// under the normal query filters. A manual stage is never touched (<see cref="Opportunity.ApplyClassification"/>).</item>
/// <item><b>Clear the flag</b> in the same commit. A message that arrives meanwhile cannot be lost: ingestion rewrites the request on the
/// conversation row, which waits for this lock and then marks it flagged again.</item>
/// </list>
/// Any exception rolls everything back and is recorded in a fresh transaction (failure count and time of the attempt, nothing else); at the maximum
/// the conversation is no longer claimed, until a new message gives it a fresh start. Customer text is read and classified; it is never logged, and neither is the message of an exception (see <see cref="ClassificationErrors"/>).
/// </summary>
public sealed class ConversationClassificationRunner(
    IServiceScopeFactory scopeFactory,
    ClassificationOptions options,
    TimeProvider timeProvider,
    ILogger<ConversationClassificationRunner> logger)
{
    /// <summary>The most messages one classification reads (the latest ones): the work per conversation is bounded.</summary>
    public const int MaxMessagesPerConversation = 200;

    /// <summary>The most characters of a message that are read from the database (the first ones): a message is bounded too.</summary>
    public const int MaxCharactersPerMessage = 1000;

    /// <summary>Returns true when a conversation was claimed (classified, or its failure recorded), false when none was due.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(options.ProcessingTimeoutSeconds));

        Guid? claimedId = null;
        DateTime? requestedAt = null;
        var tenantId = Guid.Empty;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(budget.Token);
            await SetTimeoutsAsync(db, budget.Token);

            var now = UtcNow();
            var conversation = await ClaimAsync(db, now, budget.Token);
            if (conversation is null)
            {
                return false;
            }

            claimedId = conversation.Id;
            requestedAt = conversation.ClassificationRequestedAt;
            tenantId = conversation.TenantId;
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(conversation.TenantId);

            var snapshot = await ReadSnapshotAsync(db, conversation, budget.Token);
            var opportunity = await db.Opportunities.SingleAsync(o => o.ConversationId == conversation.Id, budget.Token);
            var result = await scope.ServiceProvider.GetRequiredService<IConversationClassifier>().ClassifyAsync(snapshot, budget.Token);

            opportunity.ApplyClassification(ToUpdate(result), now);
            conversation.CompleteClassification(now);
            await db.SaveChangesAsync(budget.Token);
            await transaction.CommitAsync(budget.Token);

            logger.LogDebug("Conversation {ConversationId} classified: {Outcome}.", conversation.Id, result.Outcome);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the host is stopping; the flag was never cleared, so the conversation is claimed again later
        }
        catch (Exception ex) when (claimedId is not null)
        {
            await RecordFailureAsync(claimedId.Value, requestedAt, tenantId, ex);
            return true;
        }
    }

    private async Task<Conversation?> ClaimAsync(ApplicationDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var quietSince = now - options.Debounce;
        var retryFrom = now - options.RetryDelay;

        // Across all tenants on purpose: the backlog is shared. LIMIT is inside the locking query, so only the claimed row is locked.
        var claimed = await db.Conversations
            .FromSql($"""
                SELECT * FROM "Conversations"
                WHERE "ClassificationDirty"
                  AND "ClassificationFailures" < {options.MaxFailures}
                  AND COALESCE("ClassificationRequestedAt", "LastMessageAt") <= {quietSince}
                  AND ("ClassificationAttemptedAt" IS NULL OR "ClassificationAttemptedAt" <= {retryFrom})
                ORDER BY COALESCE("ClassificationRequestedAt", "LastMessageAt")
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        return claimed.FirstOrDefault();
    }

    private static async Task<ConversationSnapshot> ReadSnapshotAsync(ApplicationDbContext db, Conversation conversation, CancellationToken cancellationToken)
    {
        // The latest messages, then put back in conversation order. SentAt has a resolution of one second, so the tie-breakers (arrival,
        // then id) only make the order stable.
        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id)
            .OrderByDescending(m => m.SentAt).ThenByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(MaxMessagesPerConversation)
            .Select(m => new SnapshotMessage(m.Direction, m.Type, m.Text == null ? null : m.Text.Substring(0, MaxCharactersPerMessage), m.SentAt))
            .ToListAsync(cancellationToken);
        messages.Reverse();

        var branchName = await db.Branches.AsNoTracking()
            .Where(b => b.Id == conversation.BranchId)
            .Select(b => b.Name)
            .FirstOrDefaultAsync(cancellationToken);

        return new ConversationSnapshot(
            conversation.Id, branchName, conversation.StartedAt, conversation.FirstInboundAt, conversation.FirstResponseAt, messages);
    }

    /// <summary>What the opportunity stores of a result. The rule codes are joined within the column; the outcome becomes the stage (Undetermined: none).</summary>
    public static ClassificationUpdate ToUpdate(ClassificationResult result)
    {
        var stage = result.Outcome switch
        {
            ClassificationOutcome.InFollowUp => Domain.Enums.OpportunityStage.InFollowUp,
            ClassificationOutcome.Won => Domain.Enums.OpportunityStage.Won,
            ClassificationOutcome.Lost => Domain.Enums.OpportunityStage.Lost,
            _ => (Domain.Enums.OpportunityStage?)null,
        };

        return new ClassificationUpdate(
            result.ServiceInterest,
            result.InterestReason,
            result.Objection,
            stage,
            stage == Domain.Enums.OpportunityStage.Lost ? result.LossReason : null,
            result.Confidence,
            JoinWithin(result.RuleCodes, Opportunity.ClassificationRulesMaxLength));
    }

    // Whole codes only: a code cut in half would be a different, meaningless code.
    private static string? JoinWithin(IReadOnlyList<string> codes, int maxLength)
    {
        var joined = new System.Text.StringBuilder();
        foreach (var code in codes)
        {
            var next = joined.Length == 0 ? code.Length : joined.Length + 1 + code.Length;
            if (next > maxLength)
            {
                break;
            }

            if (joined.Length > 0)
            {
                joined.Append(',');
            }

            joined.Append(code);
        }

        return joined.Length == 0 ? null : joined.ToString();
    }

    private async Task SetTimeoutsAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        // set_config(..., is_local = true) is SET LOCAL with a parameter: the settings end with the transaction.
        var value = $"{options.ProcessingTimeoutSeconds}s";
        await db.Database.ExecuteSqlAsync($"SELECT set_config('statement_timeout', {value}, true)", cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT set_config('idle_in_transaction_session_timeout', {value}, true)", cancellationToken);
    }

    // ---- Failure bookkeeping: a fresh scope and transaction, no tenant context (the conversation is addressed by id). Never throws. ----

    internal async Task RecordFailureAsync(Guid conversationId, DateTime? requestedAt, Guid tenantId, Exception exception)
    {
        var error = ClassificationErrors.Describe(exception);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = UtcNow();

            // Only while the request this attempt was made for is still the current one. A message that arrived meanwhile re-requested the
            // classification with a fresh start (zero failures, no delay): counting this failure would take that start away.
            var recorded = await db.Conversations.IgnoreQueryFilters()
                .Where(c => c.Id == conversationId && c.ClassificationRequestedAt == requestedAt)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(c => c.ClassificationFailures, c => c.ClassificationFailures + 1)
                    .SetProperty(c => c.ClassificationAttemptedAt, now), CancellationToken.None);
            if (recorded == 0)
            {
                logger.LogInformation(
                    "Classifying conversation {ConversationId} failed, but it was requested again meanwhile: the failure is not counted and it will be read again: {Error}",
                    conversationId, error);
                return;
            }

            var failures = await db.Conversations.IgnoreQueryFilters().Where(c => c.Id == conversationId)
                .Select(c => c.ClassificationFailures).FirstOrDefaultAsync(CancellationToken.None);

            if (failures >= options.MaxFailures)
            {
                logger.LogWarning(
                    "Conversation {ConversationId} of tenant {TenantId} failed to be classified {Failures} times in a row and will not be tried again until it receives a new message: {Error}",
                    conversationId, tenantId, failures, error);
            }
            else
            {
                logger.LogWarning(
                    "Classifying conversation {ConversationId} failed (attempt {Failures} of {MaxFailures}); it will be tried again in {Delay} s: {Error}",
                    conversationId, failures, options.MaxFailures, options.RetryDelaySeconds, error);
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Could not record the failed classification of conversation {ConversationId}; it will be claimed again: {Error}", conversationId, ClassificationErrors.Describe(ex));
        }
    }

    private DateTime UtcNow() => DatabaseTime.Truncate(timeProvider.GetUtcNow().UtcDateTime);
}
