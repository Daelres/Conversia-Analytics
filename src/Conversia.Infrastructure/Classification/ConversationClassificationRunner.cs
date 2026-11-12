using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
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
    ILogger<ConversationClassificationRunner> logger,
    ClassificationCoordinator? coordinator = null)
{
    /// <summary>The most messages one classification reads (the latest ones): the work per conversation is bounded.</summary>
    public const int MaxMessagesPerConversation = 200;

    /// <summary>The most characters of a message that are read from the database (the first ones): a message is bounded too.</summary>
    public const int MaxCharactersPerMessage = 1000;

    /// <summary>What the LLM path reads per message: a margin beyond the cap, so a number straddling it is masked before the cap is applied.</summary>
    public const int ReadCharactersPerMessage = MaxCharactersPerMessage + 64;

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
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await using var transaction = await db.Database.BeginTransactionAsync(budget.Token);
            await SetTimeoutsAsync(db, budget.Token);

            var now = UtcNow();
            var conversation = await ClaimAsync(db, now, budget.Token);
            if (conversation is null)
            {
                if (options.Llm is null)
                {
                    return false;
                }

                // Nothing to classify: write a profile a previous run committed but could not write (failure or crash), if one is due.
                await transaction.RollbackAsync(budget.Token);
                return await WritePendingProfileAsync(cancellationToken);
            }

            claimedId = conversation.Id;
            requestedAt = conversation.ClassificationRequestedAt;
            tenantId = conversation.TenantId;
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(conversation.TenantId);

            if (options.Llm is not null)
            {
                var (decision, profile) = await ClassifyWithLlmAsync(scope.ServiceProvider, db, conversation, now, budget.Token);
                await transaction.CommitAsync(budget.Token);
                LogDecision(conversation, decision, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (profile is not null)
                {
                    await WriteProfileAsync(conversation.Id, conversation.ContactId, conversation.TenantId, profile, now, cancellationToken);
                }

                return true;
            }

            var snapshot = await ReadSnapshotAsync(db, conversation, includeKnownValues: false, budget.Token);
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
        var quietSince = now - options.EffectiveDebounce;
        var retryFrom = now - options.RetryDelay;

        // Across all tenants on purpose: the backlog is shared. LIMIT is inside the locking query, so only the claimed row is locked.
        // Conversations of a pending or suspended company wait, flagged, until it is Active again.
        var active = nameof(TenantStatus.Active);
        var claimed = await db.Conversations
            .FromSql($"""
                SELECT * FROM "Conversations"
                WHERE "ClassificationDirty"
                  AND EXISTS (SELECT 1 FROM "Tenants" WHERE "Tenants"."Id" = "Conversations"."TenantId" AND "Tenants"."Status" = {active})
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

    /// <summary>
    /// The latest messages (with their ids) and the branch: exactly what the rules classifier read before, in two queries. With
    /// <paramref name="includeKnownValues"/> (the LLM path) each message is read <see cref="ReadCharactersPerMessage"/> long (a number
    /// straddling the cap is masked before the cap is applied), and one more query reads what the system already knows: the contact's
    /// display name and WhatsApp id, the assigned advisor's name and the channel number. Those values are only used to mask them before any
    /// text leaves the process and as inputs of the snapshot hash; they are never logged. Runs under the tenant's query filters (the caller
    /// sets the tenant first).
    /// </summary>
    internal static async Task<ConversationSnapshot> ReadSnapshotAsync(ApplicationDbContext db, Conversation conversation, bool includeKnownValues, CancellationToken cancellationToken)
    {
        // The latest messages, then put back in conversation order. SentAt has a resolution of one second, so the tie-breakers (arrival,
        // then id) only make the order stable.
        var characters = includeKnownValues ? ReadCharactersPerMessage : MaxCharactersPerMessage;
        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id)
            .OrderByDescending(m => m.SentAt).ThenByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(MaxMessagesPerConversation)
            .Select(m => new SnapshotMessage(m.Direction, m.Type, m.Text == null ? null : m.Text.Substring(0, characters), m.SentAt, m.Id))
            .ToListAsync(cancellationToken);
        messages.Reverse();

        var branchName = await db.Branches.AsNoTracking()
            .Where(b => b.Id == conversation.BranchId)
            .Select(b => b.Name)
            .FirstOrDefaultAsync(cancellationToken);

        if (!includeKnownValues)
        {
            return new ConversationSnapshot(
                conversation.Id, branchName, conversation.StartedAt, conversation.FirstInboundAt, conversation.FirstResponseAt, messages, conversation.TenantId);
        }

        var known = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == conversation.Id)
            .Select(c => new
            {
                ContactName = db.Contacts.Where(x => x.Id == c.ContactId).Select(x => x.DisplayName).FirstOrDefault(),
                ContactWaId = db.Contacts.Where(x => x.Id == c.ContactId).Select(x => x.WaId).FirstOrDefault(),
                AdvisorName = db.Advisors.Where(a => a.Id == c.AdvisorId).Select(a => a.Name).FirstOrDefault(),
                ChannelNumber = db.WhatsAppChannels.Where(w => w.Id == c.ChannelId).Select(w => w.DisplayPhoneNumber).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var knownNames = new[] { known?.ContactName, known?.AdvisorName, branchName }
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct(StringComparer.Ordinal).ToList();
        var knownNumbers = new[] { known?.ContactWaId, known?.ChannelNumber }
            .Select(n => n is null ? string.Empty : new string([.. n.Where(char.IsAsciiDigit)]))
            .Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        return new ConversationSnapshot(
            conversation.Id, branchName, conversation.StartedAt, conversation.FirstInboundAt, conversation.FirstResponseAt, messages,
            conversation.TenantId, knownNames, knownNumbers, known?.ContactName, known?.AdvisorName);
    }

    // ---- The LLM path (Provider=Llm, design Stage A: one worker, the call inside the claim transaction, one attempt <= 8 s) ----

    /// <summary>The profile the LLM read, to write after the commit; null when there is nothing to write.</summary>
    private sealed record ExtractedProfile(AgeRange? AgeRange, Gender Gender);

    /// <summary>
    /// Reads the snapshot WITH the known values (they decide what is masked), lets the coordinator decide, applies the decision to the
    /// opportunity and the conversation and saves (the caller commits and logs). A profile to write is also recorded as PENDING on the
    /// conversation in this same commit, so a crash before the profile write loses nothing.
    /// </summary>
    private async Task<(ClassificationDecision Decision, ExtractedProfile? Profile)> ClassifyWithLlmAsync(
        IServiceProvider services, ApplicationDbContext db, Conversation conversation, DateTime now, CancellationToken cancellationToken)
    {
        var llm = options.Llm!;
        var decide = coordinator ?? services.GetRequiredService<ClassificationCoordinator>();
        var snapshot = await ReadSnapshotAsync(db, conversation, includeKnownValues: true, cancellationToken);
        var opportunity = await db.Opportunities.SingleAsync(o => o.ConversationId == conversation.Id, cancellationToken);
        var decision = await decide.DecideAsync(
            snapshot, new ClassificationContext(conversation.TenantId, conversation.Id, conversation.IsSandbox, conversation.LastLlmSnapshotHash), now, cancellationToken);

        ExtractedProfile? profile = null;
        switch (decision.Completion)
        {
            case ClassificationCompletion.Llm:
                opportunity.ApplyClassification(ToUpdate(decision.Applied!, ClassifierProvider.Llm), now);
                conversation.CompleteLlmClassification(now, decision.SnapshotHash);
                if (decision.Applied!.AgeRange is not null || decision.Applied.Gender != Gender.Undetermined)
                {
                    profile = new ExtractedProfile(decision.Applied.AgeRange, decision.Applied.Gender);
                    conversation.RecordPendingProfile(profile.AgeRange, profile.Gender);
                }

                break;
            case ClassificationCompletion.RulesFallback:
                opportunity.ApplyClassification(ToUpdate(decision.Applied!), now);
                conversation.CompleteWithRulesFallback(decision.AttemptedAt, decision.CountAsLlmFailure, llm.MaxFallbackRetries);
                break;
            case ClassificationCompletion.Unchanged:
                conversation.CompleteLlmClassification(now, decision.SnapshotHash);
                break;
            default:
                opportunity.ApplyClassification(ToUpdate(decision.Applied!), now);
                conversation.CompleteClassification(now);
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (decision, profile);
    }

    /// <summary>
    /// One line per LLM call (Information), with ids, provider, model, status, the four token counters, the estimated USD, what the
    /// preprocessor did and how long the claim transaction held the conversation row; a decision without a call is a Debug line, except a
    /// real conversation kept away from the real client (Information, ids only). A claim transaction longer than
    /// <c>Classification:Llm:ClaimWarningMilliseconds</c> is a Warning: ingestion, edits and sends on that conversation waited that long.
    /// The sandbox flag keeps test traffic apart from real usage. Never text.
    /// </summary>
    private void LogDecision(Conversation conversation, ClassificationDecision decision, long claimMilliseconds)
    {
        if (decision.Call is not { } call)
        {
            if (decision.Reason == ClassificationCoordinator.RealClientSandboxOnly)
            {
                logger.LogInformation(
                    "Conversation {ConversationId} of tenant {TenantId} is not sandbox, so it was not sent to the real LLM client ({Reason}; set Classification:Llm:AllowRealClientNonSandbox after the privacy review): the rules read it.",
                    conversation.Id, conversation.TenantId, decision.Reason);
            }
            else
            {
                logger.LogDebug(
                    "LLM path without a call for conversation {ConversationId} of tenant {TenantId} (Sandbox {Sandbox}): {Completion}, {Reason}, claim transaction {ClaimTransactionMs} ms.",
                    conversation.Id, conversation.TenantId, conversation.IsSandbox, decision.Completion, decision.Reason, claimMilliseconds);
            }
        }
        else
        {
            var stats = call.Stats;
            logger.LogInformation(
                "LLM classification of conversation {ConversationId} of tenant {TenantId} (Sandbox {Sandbox}): provider {Provider}, model {Model}, status {Status}, " +
                "completion {Completion}, error {Error}, tokens in {InputTokens} out {OutputTokens} cache read {CacheReadTokens} cache write {CacheWriteTokens}, " +
                "estimated USD {EstimatedUsd}, {LatencyMs} ms, claim transaction {ClaimTransactionMs} ms, won downgraded {WonDowngraded}; preprocessing: messages {MessagesIn}, " +
                "characters {CharactersBefore} -> {CharactersAfter}, estimated tokens {TokensBefore} -> {TokensAfter}, dropped {Dropped}, collapsed {Collapsed}, " +
                "deduplicated {Deduplicated}, truncated {Truncated}, merged {Merged}, omitted by budget {OmittedByBudget}.",
                conversation.Id, conversation.TenantId, conversation.IsSandbox, ClassificationOptions.LlmProvider, call.ModelId, call.Status,
                decision.Completion, call.Error, call.Usage.InputTokens, call.Usage.OutputTokens, call.Usage.CacheReadTokens, call.Usage.CacheWriteTokens,
                call.EstimatedUsd, call.LatencyMs, claimMilliseconds, call.WonDowngraded, stats.MessagesIn, stats.CharactersBefore, stats.CharactersAfter,
                stats.EstimatedTokensBefore, stats.EstimatedTokensAfter, stats.MessagesDropped, stats.MessagesCollapsed, stats.MessagesDeduplicated,
                stats.MessagesTruncated, stats.MessagesMerged, stats.MessagesOmittedByBudget);
        }

        if (claimMilliseconds > options.Llm!.ClaimWarningMilliseconds)
        {
            logger.LogWarning(
                "The LLM path held conversation {ConversationId} of tenant {TenantId} for {ClaimTransactionMs} ms (threshold {ClaimWarningMilliseconds} ms): ingestion, edits and sends on it waited (Stage A limit).",
                conversation.Id, conversation.TenantId, claimMilliseconds, options.Llm.ClaimWarningMilliseconds);
        }
    }

    /// <summary>
    /// Writes the profile the LLM read AFTER the classification commit, in a short transaction of its own that takes only the Contact row
    /// (design D5: ingestion takes Contact then Conversation, so the runner never writes Contact while it holds Conversation). A manual
    /// value is never overwritten and nothing is ever erased (<see cref="Contact.ApplyExtractedProfile"/>). Then, in another short
    /// Conversation-only write, the pending marker is cleared (only if it still holds these values). If the write fails, the marker stays and
    /// <see cref="WritePendingProfileAsync"/> retries it after the retry delay, without calling the model again; a contact that no longer
    /// exists clears the marker. Never throws, except when the host is stopping.
    /// </summary>
    private async Task WriteProfileAsync(Guid conversationId, Guid contactId, Guid tenantId, ExtractedProfile profile, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(options.ProcessingTimeoutSeconds));

                await using var transaction = await db.Database.BeginTransactionAsync(budget.Token);
                await SetTimeoutsAsync(db, budget.Token);
                var contact = (await db.Contacts
                    .FromSql($"""SELECT * FROM "Contacts" WHERE "Id" = {contactId} AND "TenantId" = {tenantId} FOR UPDATE""")
                    .IgnoreQueryFilters()
                    .ToListAsync(budget.Token)).FirstOrDefault();
                if (contact is null)
                {
                    logger.LogInformation("The contact {ContactId} of conversation {ConversationId} no longer exists; its profile is not written.", contactId, conversationId);
                }
                else
                {
                    contact.ApplyExtractedProfile(profile.AgeRange, profile.Gender, now);
                    await db.SaveChangesAsync(budget.Token);
                }

                await transaction.CommitAsync(budget.Token);
            }

            await ClearPendingProfileAsync(conversationId, profile, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Writing the profile of contact {ContactId} (conversation {ConversationId}) failed; it stays pending and is written later without calling the model again: {Error}",
                contactId, conversationId, ClassificationErrors.Describe(ex));
        }
    }

    // Only the marker of THESE values: a newer reading committed meanwhile keeps its own marker.
    private async Task ClearPendingProfileAsync(Guid conversationId, ExtractedProfile profile, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Conversations.IgnoreQueryFilters()
            .Where(c => c.Id == conversationId && c.ProfilePending && c.PendingAgeRange == profile.AgeRange && c.PendingGender == profile.Gender)
            .ExecuteUpdateAsync(set => set
                .SetProperty(c => c.ProfilePending, false)
                .SetProperty(c => c.PendingAgeRange, (AgeRange?)null)
                .SetProperty(c => c.PendingGender, (Gender?)null), cancellationToken);
    }

    /// <summary>
    /// Writes one profile left pending by a failed write or a crash: claims a conversation with the marker (not flagged, past the retry delay,
    /// <c>FOR UPDATE SKIP LOCKED</c>), records the attempt time as a lease and commits (the Conversation lock is released before the Contact
    /// is taken), then writes the profile as <see cref="WriteProfileAsync"/> does. The model is not called. Returns whether one was handled.
    /// </summary>
    private async Task<bool> WritePendingProfileAsync(CancellationToken cancellationToken)
    {
        Guid conversationId, contactId, tenantId;
        ExtractedProfile profile;
        var now = UtcNow();
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(options.ProcessingTimeoutSeconds));
            await using var transaction = await db.Database.BeginTransactionAsync(budget.Token);
            await SetTimeoutsAsync(db, budget.Token);

            var retryFrom = now - options.RetryDelay;
            var active = nameof(TenantStatus.Active);
            var pending = (await db.Conversations
                .FromSql($"""
                    SELECT * FROM "Conversations"
                    WHERE "ProfilePending" AND NOT "ClassificationDirty"
                      AND EXISTS (SELECT 1 FROM "Tenants" WHERE "Tenants"."Id" = "Conversations"."TenantId" AND "Tenants"."Status" = {active})
                      AND ("ClassificationAttemptedAt" IS NULL OR "ClassificationAttemptedAt" <= {retryFrom})
                    ORDER BY "ClassificationAttemptedAt" NULLS FIRST
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """)
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync(budget.Token)).FirstOrDefault();
            if (pending is null)
            {
                return false;
            }

            (conversationId, contactId, tenantId) = (pending.Id, pending.ContactId, pending.TenantId);
            profile = new ExtractedProfile(pending.PendingAgeRange, pending.PendingGender ?? Gender.Undetermined);
            await db.Conversations.IgnoreQueryFilters().Where(c => c.Id == conversationId)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.ClassificationAttemptedAt, now), budget.Token);
            await transaction.CommitAsync(budget.Token);
        }

        await WriteProfileAsync(conversationId, contactId, tenantId, profile, now, cancellationToken);
        return true;
    }

    /// <summary>What the opportunity stores of a result. The rule codes are joined within the column; the outcome becomes the stage (Undetermined: none).</summary>
    /// <param name="provider">Who produced it: the per-field provenance (<c>*ClassifiedBy</c>). A rules fallback (<see cref="ClassificationResult.IsRulesFallback"/>) is always Rules.</param>
    public static ClassificationUpdate ToUpdate(ClassificationResult result, ClassifierProvider provider = ClassifierProvider.Rules)
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
            JoinWithin(result.RuleCodes, Opportunity.ClassificationRulesMaxLength),
            provider,
            result.IsRulesFallback);
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
