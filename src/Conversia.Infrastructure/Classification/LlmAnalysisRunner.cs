using System.Collections.Concurrent;
using System.Diagnostics;
using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Classification.Scheduling;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Classification;

/// <summary>A company whose analysis window is open: what the runner claims within.</summary>
public sealed record TenantWork(Guid TenantId, AiAnalysisMode Mode, AnalysisWindow Window);

/// <summary>What one step of the runner did.</summary>
public enum LlmStep
{
    /// <summary>Nothing of this company was due.</summary>
    NothingDue,

    /// <summary>A conversation was handled without a call (unchanged, skipped, not admitted, deferred by the breaker).</summary>
    Handled,

    /// <summary>A call was made, or the conversation went in a batch: what the per-window cap counts.</summary>
    Called,
}

/// <summary>The requests of a batch being assembled: the rows are in the database, the prompts only here until the batch is sent.</summary>
public sealed class LlmBatchAssembly
{
    internal Guid? BatchId { get; set; }

    internal List<LlmBatchItem> Items { get; } = [];

    /// <summary>The upload was attempted (successfully or not): never send this assembly again.</summary>
    internal bool Attempted { get; set; }

    public int Count => Items.Count;
}

/// <summary>
/// The LLM analysis of one conversation at a time, in three phases (design H1, docs/CLASSIFICATION.md, "When the AI reads"), outside the
/// conversation row lock:
/// <list type="number">
/// <item><b>Phase 1</b> (short transaction): claim a pending conversation of the company <c>FOR UPDATE SKIP LOCKED</c> (quiet for
/// <c>Classification:Llm:DebounceSeconds</c>, not leased, not attempted yet in this window unless near real time), read the snapshot, let the
/// coordinator plan (admission, call reduction, breaker). A decision without a call is applied at once. A call takes a LEASE
/// (<c>LlmLeaseUntil</c>) and the transaction commits: no lock and no connection are held during the call.</item>
/// <item><b>Phase 2</b> (no lock): pseudonymize, call, record the usage (its own scope).</item>
/// <item><b>Phase 3</b> (short transaction): relock the conversation by id with <c>SKIP LOCKED</c> (three tries 200 ms apart; else the lease
/// expires and the conversation is claimed again). Apply only if the conversation still holds THIS lease and is pending, the company is
/// still active with the AI on, and the snapshot hash, <c>ClassificationRequestedAt</c> and the opportunity's stage are unchanged since phase
/// 1; otherwise DISCARD (the conversation stays pending).</item>
/// </list>
/// The contact profile is written after the commit (Contact row only, design D5). In batch mode phase 1 adds the request to a Message Batch
/// and phase 3 runs when the batch result is collected; each batch is worked by one process at a time (a PostgreSQL advisory lock) and each
/// request by one collector (an atomic Pending → Collecting step), so a result is applied and its usage recorded once.
/// </summary>
public sealed class LlmAnalysisRunner(
    IServiceScopeFactory scopeFactory,
    ClassificationOptions options,
    TimeProvider timeProvider,
    ILogger<LlmAnalysisRunner> logger,
    ClassificationCoordinator? coordinator = null,
    ILlmBatchClient? batchClient = null)
{
    /// <summary>Phase 3 tries to relock this many times, this far apart, before it leaves the conversation to its lease.</summary>
    public const int RelockAttempts = 3;

    public static readonly TimeSpan RelockDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>Saving the provider's id after an accepted upload is tried this many times before it is left to recovery.</summary>
    public const int SubmitSaveAttempts = 3;

    // Batches this process is uploading right now: recovery never touches them, whatever their age.
    private static readonly ConcurrentDictionary<Guid, byte> Uploading = new();

    private LlmOptions Llm => options.Llm ?? throw new InvalidOperationException("The LLM runner needs Classification:Provider=Llm.");

    /// <summary>Whether a Nightly company's conversations go in Message Batches (<c>Classification:Llm:Batch:Enabled</c> and a batch client).</summary>
    public bool BatchMode => batchClient is not null && Llm.BatchEnabled;

    // ---- One synchronous analysis --------------------------------------------------------------------------------------

    /// <summary>Claims and analyses one due conversation of the company, synchronously (three phases).</summary>
    public async Task<LlmStep> ProcessNextAsync(TenantWork work, CancellationToken cancellationToken = default)
    {
        var claim = await ClaimAndPlanAsync(work, batch: null, cancellationToken);
        if (claim is null)
        {
            return LlmStep.NothingDue;
        }

        if (claim.Plan.Resolved is not null)
        {
            return LlmStep.Handled;
        }

        await CallAndApplyAsync(claim, cancellationToken);
        return LlmStep.Called;
    }

    /// <summary>Claims one due conversation of a Nightly company for the batch being assembled (or analyses it synchronously when a batch
    /// already failed or was stale for it: the fallback). Sends nothing yet.</summary>
    public async Task<LlmStep> PlanBatchItemAsync(TenantWork work, LlmBatchAssembly assembly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var claim = await ClaimAndPlanAsync(work, assembly, cancellationToken);
        if (claim is null)
        {
            return LlmStep.NothingDue;
        }

        if (claim.Plan.Resolved is not null)
        {
            return LlmStep.Handled;
        }

        if (!claim.Batched)
        {
            await CallAndApplyAsync(claim, cancellationToken);
        }

        return LlmStep.Called;
    }

    private async Task CallAndApplyAsync(Claim claim, CancellationToken cancellationToken)
    {
        // Phase 2: no lock, no connection.
        var started = Stopwatch.GetTimestamp();
        var decision = await claim.Coordinator.ExecuteAsync(claim.Plan, claim.Context, claim.Now, cancellationToken);
        var callMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        await RecordAsync(claim.Work, UtcNow(), RunCounter.Calls);
        await ApplyAsync(claim, decision, callMs, cancellationToken);
    }

    /// <summary>What phase 1 saw and decided.</summary>
    private sealed record Claim(
        Guid ConversationId, Guid TenantId, Guid ContactId, bool IsSandbox, DateTime? RequestedAt, OpportunityStage Stage, DateTime? StageChangedAt,
        StageSource StageSource, LlmPlan Plan, ClassificationContext Context, DateTime Now, TenantWork Work, long ClaimMs, bool Batched,
        ClassificationCoordinator Coordinator, DateTime? Lease);

    private async Task<Claim?> ClaimAndPlanAsync(TenantWork work, LlmBatchAssembly? batch, CancellationToken cancellationToken)
    {
        var llm = Llm;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var decide = coordinator ?? scope.ServiceProvider.GetRequiredService<ClassificationCoordinator>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(options.ProcessingTimeoutSeconds));

        var started = Stopwatch.GetTimestamp();
        await using var transaction = await db.Database.BeginTransactionAsync(budget.Token);
        await ConversationClassificationRunner.SetTimeoutsAsync(db, options.ProcessingTimeoutSeconds, budget.Token);
        var now = UtcNow();

        var quietSince = now - llm.Debounce;
        var scheduled = work.Mode != AiAnalysisMode.NearRealTime;
        var windowStart = work.Window.Start;
        var retryFrom = now - options.RetryDelay;
        // One analysis per window: a scheduled window never claims a conversation already attempted inside it, however many messages arrive.
        // Near real time: an attempt is retried after the retry delay (as in the first stage). Ordered by the request time, which every
        // pending conversation has (RequestClassification sets it; the migration backfilled it): the partial index IX_Conversations_LlmPending.
        var conversation = (await db.Conversations
            .FromSql($"""
                SELECT * FROM "Conversations"
                WHERE "TenantId" = {work.TenantId} AND "LlmPending"
                  AND "ClassificationRequestedAt" <= {quietSince}
                  AND ("LlmLeaseUntil" IS NULL OR "LlmLeaseUntil" <= {now})
                  AND ("LlmAttemptedAt" IS NULL OR ({scheduled} AND "LlmAttemptedAt" < {windowStart}) OR (NOT {scheduled} AND "LlmAttemptedAt" <= {retryFrom}))
                ORDER BY "ClassificationRequestedAt"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .IgnoreQueryFilters()
            .ToListAsync(budget.Token)).FirstOrDefault();
        if (conversation is null)
        {
            return null;
        }

        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(conversation.TenantId);
        var snapshot = await ConversationClassificationRunner.ReadSnapshotAsync(db, conversation, includeKnownValues: true, budget.Token);
        var opportunity = await db.Opportunities.AsNoTracking().SingleAsync(o => o.ConversationId == conversation.Id, budget.Token);
        var context = new ClassificationContext(conversation.TenantId, conversation.Id, conversation.IsSandbox, conversation.LastLlmSnapshotHash);

        // Batch only for a nightly company, and only while no batch failed for this very request, nor was found stale in this window (the
        // fallback: one synchronous read).
        var useBatch = batch is not null && work.Mode == AiAnalysisMode.Nightly && BatchMode
            && !await db.LlmBatchRequests.AnyAsync(
                r => r.ConversationId == conversation.Id
                     && ((r.RequestedAt == conversation.ClassificationRequestedAt
                          && (r.Status == LlmBatchRequestStatus.Errored || r.Status == LlmBatchRequestStatus.Expired || r.Status == LlmBatchRequestStatus.Canceled))
                         || (r.Status == LlmBatchRequestStatus.Discarded && r.WindowStart == windowStart)),
                budget.Token);

        var plan = await decide.PlanAsync(snapshot, context, now, useBreaker: !useBatch, budget.Token);
        var outcome = RunCounter.Claimed;
        DateTime? lease = null;
        try
        {
            if (plan.Resolved is { } resolved)
            {
                outcome = Resolve(conversation, resolved, now, scheduled);
            }
            else if (useBatch)
            {
                batch!.BatchId ??= await PrepareBatchAsync(now, cancellationToken);
                lease = now + LlmOptions.BatchLease;
                db.LlmBatchRequests.Add(LlmBatchRequest.For(
                    batch.BatchId.Value, conversation.TenantId, conversation.Id, windowStart, plan.Prepared!.SnapshotHash, conversation.IsSandbox,
                    conversation.ClassificationRequestedAt, opportunity.Stage, opportunity.StageChangedAt, now, lease, EstimateBatchedUsd(plan.Prepared)));
                conversation.LeaseLlmAnalysis(lease.Value);
                outcome = RunCounter.Batched;
            }
            else
            {
                lease = now + LlmOptions.LeaseFor(options.RetryDelay);
                conversation.LeaseLlmAnalysis(lease.Value);
            }

            await db.SaveChangesAsync(budget.Token);
            await transaction.CommitAsync(budget.Token);
        }
        catch
        {
            decide.Release(plan);
            throw;
        }

        if (useBatch && plan.NeedsCall)
        {
            var prepared = plan.Prepared!;
            batch!.Items.Add(new LlmBatchItem(
                LlmBatchRequest.CustomIdFor(conversation.Id, prepared.SnapshotHash),
                new LlmRequest(prepared.Prompt.SystemPrompt, prepared.Prompt.ContextBlock, prepared.Prompt.UserContent, prepared.Prompt.Schema, prepared.SnapshotHash)));
        }

        var claimMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        await RecordAsync(work, now, RunCounter.Claimed);
        if (outcome != RunCounter.Claimed)
        {
            await RecordAsync(work, now, outcome);
        }

        if (outcome == RunCounter.Batched)
        {
            await RecordAsync(work, now, RunCounter.Calls);
        }

        if (plan.Resolved is { } done)
        {
            LogWithoutCall(conversation, done, claimMs);
        }

        return new Claim(
            conversation.Id, conversation.TenantId, conversation.ContactId, conversation.IsSandbox, conversation.ClassificationRequestedAt, opportunity.Stage,
            opportunity.StageChangedAt, opportunity.StageSource, plan, context, now, work, claimMs, useBatch && plan.NeedsCall, decide, lease);
    }

    /// <summary>
    /// What a batched request is reserved at in admission until it is resolved (review: no overshoot by a whole batch): the estimated input
    /// of its prompt and <see cref="LlmOptions.EstimatedOutputTokens"/> of answer, at the batch price.
    /// </summary>
    private decimal EstimateBatchedUsd(PreparedLlmCall prepared)
    {
        var prompt = prepared.Prompt;
        var input = PromptFormat.EstimateTokens(prompt.SystemPrompt.Length + prompt.ContextBlock.Length + prompt.UserContent.Length);
        return Llm.Pricing.Estimate(new LlmUsage(input, LlmOptions.EstimatedOutputTokens, 0, 0)) * Llm.BatchDiscountFactor;
    }

    /// <summary>A decision without a call, applied in phase 1.</summary>
    private RunCounter Resolve(Conversation conversation, ClassificationDecision decision, DateTime now, bool scheduled)
    {
        switch (decision.Completion)
        {
            case ClassificationCompletion.Unchanged:
                conversation.ConfirmLlmAnalysisUnchanged(now);
                return RunCounter.Unchanged;
            case ClassificationCompletion.RulesFallback:
                // Breaker open or probe in flight: claimable again about when the coordinator said (its attempt time is "retry-delay earlier").
                conversation.DeferLlmAnalysis(decision.AttemptedAt + options.RetryDelay);
                return RunCounter.Deferred;
            case ClassificationCompletion.Rules when decision.Reason.StartsWith("not_admitted:", StringComparison.Ordinal):
                // The company may not use the AI right now (quota, ceiling, cap, switch): the work is not lost. Scheduled: the next window;
                // near real time: an hour later (not every retry delay).
                if (scheduled)
                {
                    conversation.DiscardLlmAnalysis(now);
                }
                else
                {
                    conversation.DeferLlmAnalysis(now + LlmOptions.NotAdmittedRetry);
                }

                return RunCounter.Skipped;
            default:
                // No customer text, sandbox excluded, kept from the real client: the rules (already applied) stand.
                conversation.SkipLlmAnalysis(now);
                return RunCounter.Skipped;
        }
    }

    // ---- Phase 3 --------------------------------------------------------------------------------------------------------

    private async Task ApplyAsync(Claim claim, ClassificationDecision decision, long callMs, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = await RelockAndApplyAsync(
            claim.ConversationId, claim.TenantId, claim.RequestedAt, claim.Stage, claim.StageChangedAt, claim.StageSource, decision.SnapshotHash ?? claim.Plan.Prepared!.SnapshotHash,
            claim.Lease, claim.Coordinator, decision, cancellationToken);
        var applyMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (outcome.Applied is { } counter)
        {
            await RecordAsync(claim.Work, UtcNow(), counter);
        }

        LogCall(claim.ConversationId, claim.TenantId, claim.IsSandbox, claim.Work.Mode, batched: false, decision, outcome.Applied, claim.ClaimMs, callMs, applyMs);
        if (outcome.Profile is not null)
        {
            await WriteProfileAsync(claim.ConversationId, claim.ContactId, claim.TenantId, outcome.Profile, UtcNow(), cancellationToken);
        }
    }

    /// <summary>The profile the LLM read, to write after the commit; null when there is nothing to write.</summary>
    private sealed record ExtractedProfile(AgeRange? AgeRange, Gender Gender);

    /// <summary>What phase 3 did: the run counter (null: the row stayed locked), the profile to write, and the live conversation's facts.</summary>
    private sealed record Phase3(RunCounter? Applied, ExtractedProfile? Profile, bool IsSandbox, Guid? ContactId);

    /// <summary>
    /// Phase 3: relocks the conversation (SKIP LOCKED, a few tries), checks that the answer is still the conversation's business and that nothing
    /// changed since phase 1, and applies the decision.
    /// </summary>
    private async Task<Phase3> RelockAndApplyAsync(
        Guid conversationId, Guid tenantId, DateTime? requestedAt, OpportunityStage stage, DateTime? stageChangedAt, StageSource? stageSource, string expectedHash,
        DateTime? expectedLease, ClassificationCoordinator decide, ClassificationDecision decision, CancellationToken cancellationToken)
    {
        var llm = Llm;
        for (var attempt = 1; attempt <= RelockAttempts; attempt++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(options.ProcessingTimeoutSeconds));
            await using var transaction = await db.Database.BeginTransactionAsync(budget.Token);
            await ConversationClassificationRunner.SetTimeoutsAsync(db, options.ProcessingTimeoutSeconds, budget.Token);

            var conversation = (await db.Conversations
                .FromSql($"""SELECT * FROM "Conversations" WHERE "Id" = {conversationId} AND "TenantId" = {tenantId} FOR UPDATE SKIP LOCKED""")
                .IgnoreQueryFilters()
                .ToListAsync(budget.Token)).FirstOrDefault();
            if (conversation is null)
            {
                await transaction.RollbackAsync(budget.Token);
                var exists = await db.Conversations.IgnoreQueryFilters().AnyAsync(c => c.Id == conversationId, cancellationToken);
                if (!exists)
                {
                    return new Phase3(RunCounter.Discarded, null, false, null); // gone (suppressed, deleted): nothing to apply
                }

                if (attempt < RelockAttempts)
                {
                    await Task.Delay(RelockDelay, cancellationToken);
                }

                continue;
            }

            var now = UtcNow();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var opportunity = await db.Opportunities.SingleAsync(o => o.ConversationId == conversationId, budget.Token);
            var snapshot = await ConversationClassificationRunner.ReadSnapshotAsync(db, conversation, includeKnownValues: true, budget.Token);
            var currentHash = decide.Prepare(snapshot).SnapshotHash;
            var company = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => new { t.Status, t.AiEnabled }).FirstOrDefaultAsync(budget.Token);

            // Still this answer's conversation: pending, under the lease phase 1 took (an expired lease another worker replaced, or a reading
            // applied meanwhile, means someone else owns it now), the company still active with the AI on, and the real client still allowed
            // for it. Then nothing it read changed: a message (hash and request time), an auto-close or a person (stage).
            var notOurs = !conversation.LlmPending || conversation.LlmLeaseUntil != expectedLease;
            var notAllowed = company is null || company.Status != TenantStatus.Active || !company.AiEnabled || (llm.SandboxOnly && !conversation.IsSandbox);
            var stale = !string.Equals(currentHash, expectedHash, StringComparison.Ordinal)
                || conversation.ClassificationRequestedAt != requestedAt
                || opportunity.Stage != stage || opportunity.StageChangedAt != stageChangedAt
                || (stageSource is { } source && opportunity.StageSource != source);

            RunCounter counter;
            ExtractedProfile? profile = null;
            if (notOurs)
            {
                await transaction.RollbackAsync(budget.Token); // somebody else's: touch nothing, not even the lease
                return new Phase3(RunCounter.Discarded, null, conversation.IsSandbox, conversation.ContactId);
            }

            if (notAllowed || stale)
            {
                conversation.DiscardLlmAnalysis(now);
                counter = RunCounter.Discarded;
            }
            else
            {
                switch (decision.Completion)
                {
                    case ClassificationCompletion.Llm:
                        opportunity.ApplyClassification(ConversationClassificationRunner.ToUpdate(decision.Applied!, ClassifierProvider.Llm), now);
                        conversation.CompleteLlmAnalysis(now, decision.SnapshotHash!);
                        if (decision.Applied!.AgeRange is not null || decision.Applied.Gender != Gender.Undetermined)
                        {
                            profile = new ExtractedProfile(decision.Applied.AgeRange, decision.Applied.Gender);
                            conversation.RecordPendingProfile(profile.AgeRange, profile.Gender);
                        }

                        counter = RunCounter.Analysed;
                        break;
                    case ClassificationCompletion.RulesFallback when decision.CountAsLlmFailure:
                        // The rules reading is already on the opportunity (the near-real-time pass): nothing to write there.
                        conversation.RecordLlmContentFailure(now, llm.MaxFallbackRetries);
                        counter = RunCounter.Failed;
                        break;
                    case ClassificationCompletion.RulesFallback:
                        // Transport failure or misconfiguration: retried after the retry delay, inside the window when it is still open.
                        conversation.DeferLlmAnalysis(now + options.RetryDelay);
                        counter = RunCounter.Deferred;
                        break;
                    case ClassificationCompletion.Unchanged:
                        conversation.ConfirmLlmAnalysisUnchanged(now);
                        counter = RunCounter.Unchanged;
                        break;
                    default:
                        conversation.SkipLlmAnalysis(now);
                        counter = RunCounter.Skipped;
                        break;
                }
            }

            await db.SaveChangesAsync(budget.Token);
            await transaction.CommitAsync(budget.Token);
            return new Phase3(counter, profile, conversation.IsSandbox, conversation.ContactId);
        }

        logger.LogInformation(
            "Conversation {ConversationId} of tenant {TenantId} stayed locked after the LLM call ({Attempts} tries): its answer is left, the lease expires and it is analysed again.",
            conversationId, tenantId, RelockAttempts);
        return new Phase3(null, null, false, null);
    }

    // ---- Batch mode: assembling and sending ----------------------------------------------------------------------------

    private async Task<Guid> PrepareBatchAsync(DateTime now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var batch = LlmBatch.Prepare(1, now);
        db.LlmBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        return batch.Id;
    }

    /// <summary>
    /// Sends the assembled batch, once (the SDK never retries it: creating a batch is not idempotent). The upload start is saved first (from
    /// then on recovery looks for the batch at the provider); after the provider accepted it, its id is logged at once and saved, a few times
    /// if needed, only onto a batch still Preparing (one failed meanwhile is canceled at the provider instead). A failure to send fails the
    /// batch and releases its conversations, which take the synchronous path; a cancelled upload (the host is stopping) leaves it to recovery.
    /// </summary>
    public async Task SubmitBatchAsync(LlmBatchAssembly assembly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (assembly.Attempted || assembly.BatchId is not { } batchId || assembly.Count == 0 || batchClient is null)
        {
            return;
        }

        assembly.Attempted = true;
        Uploading[batchId] = 0;
        try
        {
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var started = UtcNow();
                var marked = await db.LlmBatches.Where(b => b.Id == batchId && b.Status == LlmBatchStatus.Preparing)
                    .ExecuteUpdateAsync(set => set.SetProperty(b => b.SubmitStartedAt, started).SetProperty(b => b.RequestCount, assembly.Count), CancellationToken.None);
                if (marked == 0)
                {
                    return; // failed meanwhile (recovered as abandoned): its conversations were released, nothing is sent
                }
            }

            LlmBatchSubmission submission;
            try
            {
                submission = await batchClient.SubmitAsync(assembly.Items, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Sending Message Batch {BatchId} was interrupted (the host is stopping); recovery will adopt or fail it.", batchId);
                return;
            }
            catch (Exception ex)
            {
                var error = ex is LlmCallException call ? $"{call.Kind}:{call.ErrorType}" : ex.GetType().Name;
                logger.LogWarning("Sending Message Batch {BatchId} ({Requests} requests) failed ({Error}); its conversations take the synchronous path.", batchId, assembly.Count, error);
                await FailBatchAsync(batchId, error, CancellationToken.None);
                return;
            }

            // The provider has it: the id is in the log before anything can fail here.
            logger.LogInformation(
                "Message Batch {BatchId} accepted by the provider as {ProviderBatchId} ({Requests} requests, expires {ExpiresAt:o}).",
                batchId, submission.ProviderBatchId, assembly.Count, submission.ExpiresAt);
            await SaveProviderIdAsync(batchId, submission, CancellationToken.None);
        }
        finally
        {
            Uploading.TryRemove(batchId, out _);
        }
    }

    private async Task SaveProviderIdAsync(Guid batchId, LlmBatchSubmission submission, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= SubmitSaveAttempts; attempt++)
        {
            try
            {
                if (await MarkSubmittedAsync(batchId, submission.ProviderBatchId, submission.ExpiresAt, cancellationToken))
                {
                    return;
                }

                // Failed meanwhile (recovered as abandoned): its conversations were released and may be read again; stop the provider's copy.
                logger.LogWarning("Message Batch {BatchId} was failed while {ProviderBatchId} was being created; the provider batch is canceled.", batchId, submission.ProviderBatchId);
                await CancelQuietlyAsync(submission.ProviderBatchId, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < SubmitSaveAttempts && ex is not OperationCanceledException)
            {
                await Task.Delay(RelockDelay, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    "Could not save the provider id {ProviderBatchId} of Message Batch {BatchId}: recovery adopts it from the provider's list. {Error}",
                    submission.ProviderBatchId, batchId, ClassificationErrors.Describe(ex));
            }
        }
    }

    /// <summary>Only onto a batch still Preparing: a batch failed meanwhile is never turned back into a submitted one.</summary>
    private async Task<bool> MarkSubmittedAsync(Guid batchId, string providerBatchId, DateTime? expiresAt, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = UtcNow();
        var expires = expiresAt is null ? (DateTime?)null : DatabaseTime.Truncate(DateTime.SpecifyKind(expiresAt.Value, DateTimeKind.Utc));
        return await db.LlmBatches.Where(b => b.Id == batchId && b.Status == LlmBatchStatus.Preparing)
            .ExecuteUpdateAsync(set => set
                .SetProperty(b => b.Status, LlmBatchStatus.Submitted)
                .SetProperty(b => b.ProviderBatchId, providerBatchId)
                .SetProperty(b => b.SubmittedAt, now)
                .SetProperty(b => b.ExpiresAt, expires), cancellationToken) == 1;
    }

    private async Task CancelQuietlyAsync(string providerBatchId, CancellationToken cancellationToken)
    {
        try
        {
            await batchClient!.CancelAsync(providerBatchId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Canceling provider batch {ProviderBatchId} failed: {Error}", providerBatchId, ex is LlmCallException c ? $"{c.Kind}:{c.ErrorType}" : ex.GetType().Name);
        }
    }

    /// <summary>A batch that could not be sent, was abandoned or can no longer be read: its unresolved requests are marked errored and their
    /// conversations released (only those still holding the request's lease), so the next claim of each takes the synchronous path.</summary>
    private async Task FailBatchAsync(Guid batchId, string error, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = UtcNow();
        var batch = await db.LlmBatches.SingleAsync(b => b.Id == batchId, cancellationToken);
        batch.MarkFailed(error, now);
        var requests = await db.LlmBatchRequests.IgnoreQueryFilters()
            .Where(r => r.BatchId == batchId && (r.Status == LlmBatchRequestStatus.Pending || r.Status == LlmBatchRequestStatus.Collecting))
            .ToListAsync(cancellationToken);
        foreach (var request in requests)
        {
            request.Resolve(LlmBatchRequestStatus.Errored, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var request in requests)
        {
            await ReleaseLeaseAsync(db, request.ConversationId, request.LeaseUntil, cancellationToken);
        }
    }

    // ---- Batch mode: recovery ------------------------------------------------------------------------------------------

    /// <summary>
    /// Batches a crash, a failed save or a stopping host left <c>Preparing</c> (older than <see cref="LlmOptions.BatchPreparingTimeout"/>
    /// since the upload started, never while this process uploads them). Never uploaded: failed, conversations released. Uploaded: the
    /// provider's list is read back to the upload's start; a batch none of ours with as many requests (and, when it ended, only our custom ids)
    /// is ADOPTED and collected as usual; other unknown batches of that moment are canceled; with no match the batch is failed. Returns how
    /// many batches were resolved.
    /// </summary>
    public async Task<int> RecoverAbandonedBatchesAsync(CancellationToken cancellationToken = default)
    {
        List<LlmBatch> abandoned;
        HashSet<string> known;
        var before = UtcNow() - LlmOptions.BatchPreparingTimeout;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            abandoned = await db.LlmBatches.AsNoTracking()
                .Where(b => b.Status == LlmBatchStatus.Preparing && (b.SubmitStartedAt ?? b.PreparedAt) <= before)
                .ToListAsync(cancellationToken);
            if (abandoned.Count == 0)
            {
                return 0;
            }

            known = (await db.LlmBatches.AsNoTracking().Where(b => b.ProviderBatchId != null).Select(b => b.ProviderBatchId!).ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
        }

        var resolved = 0;
        foreach (var batch in abandoned.Where(b => !Uploading.ContainsKey(b.Id)))
        {
            await using var hold = await BatchLock.TryTakeAsync(scopeFactory, batch.Id, cancellationToken);
            if (hold is null)
            {
                continue; // another process is on it
            }

            if (batch.SubmitStartedAt is null || batchClient is null)
            {
                await FailBatchAsync(batch.Id, "abandoned", cancellationToken);
                resolved++;
                continue;
            }

            if (await AdoptOrCancelAsync(batch, known, cancellationToken) is { } adopted)
            {
                known.Add(adopted);
                logger.LogWarning("Message Batch {BatchId} was found at the provider as {ProviderBatchId} and adopted: it is collected as usual.", batch.Id, adopted);
            }
            else
            {
                await FailBatchAsync(batch.Id, "abandoned", cancellationToken);
            }

            resolved++;
        }

        return resolved;
    }

    private async Task<string?> AdoptOrCancelAsync(LlmBatch batch, HashSet<string> known, CancellationToken cancellationToken)
    {
        var from = batch.SubmitStartedAt!.Value - TimeSpan.FromMinutes(2);
        var until = batch.SubmitStartedAt.Value + LlmOptions.BatchPreparingTimeout;
        var candidates = new List<LlmBatchSummary>();
        await foreach (var listed in batchClient!.ListAsync(cancellationToken))
        {
            if (listed.CreatedAt < from)
            {
                break; // newest first: everything after is older than the upload
            }

            if (listed.CreatedAt <= until && !known.Contains(listed.ProviderBatchId))
            {
                candidates.Add(listed);
            }
        }

        string? adopted = null;
        Dictionary<string, Guid>? ours = null;
        foreach (var candidate in candidates.Where(c => c.Requests == batch.RequestCount))
        {
            if (candidate.Processing == LlmBatchProcessing.Ended)
            {
                ours ??= await CustomIdsOfAsync(batch.Id, cancellationToken);
                var matches = true;
                await foreach (var result in batchClient.ResultsAsync(candidate.ProviderBatchId, cancellationToken))
                {
                    if (!ours.ContainsKey(result.CustomId))
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }
            }

            if (await MarkSubmittedAsync(batch.Id, candidate.ProviderBatchId, null, cancellationToken))
            {
                adopted = candidate.ProviderBatchId;
                break;
            }
        }

        // The other unknown batches of that moment are copies nobody will collect: stop them before they bill more.
        foreach (var orphan in candidates.Where(c => c.ProviderBatchId != adopted && c.Processing != LlmBatchProcessing.Ended))
        {
            logger.LogWarning("Provider batch {ProviderBatchId} matches no batch of ours: it is canceled.", orphan.ProviderBatchId);
            await CancelQuietlyAsync(orphan.ProviderBatchId, cancellationToken);
        }

        return adopted;
    }

    private async Task<Dictionary<string, Guid>> CustomIdsOfAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.LlmBatchRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.BatchId == batchId)
            .ToDictionaryAsync(r => r.CustomId, r => r.Id, StringComparer.Ordinal, cancellationToken);
    }

    // ---- Batch mode: polling and collecting ----------------------------------------------------------------------------

    /// <summary>
    /// Polls the submitted batches and collects the ended ones (also those a restart left half collected), each under its advisory lock (one
    /// process per batch). Each result goes through phase 3 with the same checks; its usage is recorded once, at the batch discount. A batch
    /// that cannot be read any more after its expiry plus <c>Classification:Llm:Batch:PoisonGraceHours</c> is failed. Returns the number of
    /// results applied or resolved.
    /// </summary>
    public async Task<int> PollBatchesAsync(CancellationToken cancellationToken = default)
    {
        if (batchClient is null)
        {
            return 0;
        }

        List<LlmBatch> open;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            open = await db.LlmBatches.AsNoTracking()
                .Where(b => b.Status == LlmBatchStatus.Submitted || b.Status == LlmBatchStatus.Ended)
                .OrderBy(b => b.CreatedAt).ToListAsync(cancellationToken);
        }

        var handled = 0;
        foreach (var batch in open)
        {
            await using var hold = await BatchLock.TryTakeAsync(scopeFactory, batch.Id, cancellationToken);
            if (hold is null)
            {
                continue; // another process polls or collects it
            }

            try
            {
                handled += await PollAsync(batch.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var error = ex is LlmCallException call ? $"{call.Kind}:{call.ErrorType}" : ClassificationErrors.Describe(ex);
                var deadline = (batch.ExpiresAt ?? (batch.SubmittedAt ?? batch.CreatedAt).AddHours(24)) + TimeSpan.FromHours(Llm.BatchPoisonGraceHours);
                if (UtcNow() > deadline)
                {
                    logger.LogError("Message Batch {BatchId} cannot be read any more ({Error}) after its expiry and grace: it is failed and its conversations released.", batch.Id, error);
                    await FailBatchAsync(batch.Id, $"poison:{error}", CancellationToken.None);
                }
                else
                {
                    logger.LogWarning("Polling Message Batch {BatchId} failed ({Error}); it is polled again later.", batch.Id, error);
                }
            }
        }

        return handled;
    }

    private async Task<int> PollAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        LlmBatch batch;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            batch = await db.LlmBatches.SingleAsync(b => b.Id == batchId, cancellationToken);
            if (batch.Status == LlmBatchStatus.Submitted)
            {
                var state = await batchClient!.GetAsync(batch.ProviderBatchId!, cancellationToken);
                batch.RecordPoll(now);
                if (state.Processing == LlmBatchProcessing.Ended)
                {
                    batch.MarkEnded(state.Succeeded, state.Errored, state.Expired, state.Canceled, now);
                }

                await db.SaveChangesAsync(cancellationToken);
                if (state.Processing != LlmBatchProcessing.Ended)
                {
                    return 0;
                }
            }
            else if (batch.Status != LlmBatchStatus.Ended)
            {
                return 0;
            }
        }

        return await CollectAsync(batch.Id, batch.ProviderBatchId!, cancellationToken);
    }

    private async Task<int> CollectAsync(Guid batchId, string providerBatchId, CancellationToken cancellationToken)
    {
        var handled = await ReleaseStuckCollectingAsync(batchId, cancellationToken);
        Dictionary<string, LlmBatchRequest> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            pending = await db.LlmBatchRequests.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.BatchId == batchId && r.Status == LlmBatchRequestStatus.Pending)
                .ToDictionaryAsync(r => r.CustomId, StringComparer.Ordinal, cancellationToken);
        }

        var leftLocked = 0;
        await foreach (var result in batchClient!.ResultsAsync(providerBatchId, cancellationToken))
        {
            if (!pending.Remove(result.CustomId, out var request))
            {
                continue; // already collected before a restart, or not ours
            }

            if (await CollectOneAsync(request, result, cancellationToken))
            {
                handled++;
            }
            else
            {
                leftLocked++;
            }
        }

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // A request the provider returned no result for is treated like an expired one: released for the synchronous path.
            foreach (var missing in pending.Values)
            {
                if (await TakeAsync(db, missing.Id, cancellationToken))
                {
                    await ResolveAsync(db, missing.Id, LlmBatchRequestStatus.Expired, cancellationToken);
                    await ReleaseLeaseAsync(db, missing.ConversationId, missing.LeaseUntil, cancellationToken);
                    handled++;
                }
            }

            var unresolved = await db.LlmBatchRequests.IgnoreQueryFilters()
                .AnyAsync(r => r.BatchId == batchId && (r.Status == LlmBatchRequestStatus.Pending || r.Status == LlmBatchRequestStatus.Collecting), cancellationToken);
            if (leftLocked == 0 && !unresolved)
            {
                var now = UtcNow();
                await db.LlmBatches.Where(b => b.Id == batchId && b.Status == LlmBatchStatus.Ended)
                    .ExecuteUpdateAsync(set => set.SetProperty(b => b.Status, LlmBatchStatus.Collected).SetProperty(b => b.CollectedAt, now), cancellationToken);
                logger.LogInformation("Message Batch {BatchId} collected: {Handled} results resolved.", batchId, handled);
            }
        }

        return handled;
    }

    /// <summary>Requests a collector took and never resolved (it died): resolved as failed without recording their usage again, released.</summary>
    private async Task<int> ReleaseStuckCollectingAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var before = UtcNow() - LlmOptions.CollectingTimeout;
        var stuck = await db.LlmBatchRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.BatchId == batchId && r.Status == LlmBatchRequestStatus.Collecting && r.CollectingAt <= before)
            .ToListAsync(cancellationToken);
        foreach (var request in stuck)
        {
            await ResolveAsync(db, request.Id, LlmBatchRequestStatus.Failed, cancellationToken);
            await ReleaseLeaseAsync(db, request.ConversationId, request.LeaseUntil, cancellationToken);
            logger.LogWarning(
                "Batch request of conversation {ConversationId} was left half collected (a crash): released, its usage is not recorded again.", request.ConversationId);
        }

        return stuck.Count;
    }

    /// <summary>One batch result. Returns false when its conversation stayed locked (the request goes back to Pending: collected on a later poll).</summary>
    private async Task<bool> CollectOneAsync(LlmBatchRequest request, LlmBatchResult result, CancellationToken cancellationToken)
    {
        var work = new TenantWork(request.TenantId, AiAnalysisMode.Nightly, new AnalysisWindow(request.WindowStart, request.WindowStart, request.WindowStart));
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            if (!await TakeAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), request.Id, cancellationToken))
            {
                return true; // another collector has it
            }
        }

        if (result.Kind != LlmBatchResultKind.Succeeded || result.Completion is null)
        {
            // Errored, canceled and expired requests are not billed: released, and the next claim takes the synchronous path.
            var failedStatus = result.Kind switch
            {
                LlmBatchResultKind.Expired => LlmBatchRequestStatus.Expired,
                LlmBatchResultKind.Canceled => LlmBatchRequestStatus.Canceled,
                _ => LlmBatchRequestStatus.Errored,
            };
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await ResolveAsync(db, request.Id, failedStatus, cancellationToken);
            await ReleaseLeaseAsync(db, request.ConversationId, request.LeaseUntil, cancellationToken);
            await RecordAsync(work, UtcNow(), RunCounter.Deferred);
            logger.LogInformation(
                "Batch request of conversation {ConversationId} of tenant {TenantId} came back {Result} ({Error}); it takes the synchronous path.",
                request.ConversationId, request.TenantId, failedStatus, result.ErrorType);
            return true;
        }

        await using var lookup = scopeFactory.CreateAsyncScope();
        var decide = coordinator ?? lookup.ServiceProvider.GetRequiredService<ClassificationCoordinator>();

        // Read against the snapshot as it is now: if it still hashes the same, the prompt that was sent is rebuilt exactly (same lines shown).
        Conversation? live;
        ConversationSnapshot? snapshot = null;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(request.TenantId);
            live = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ConversationId, cancellationToken);
            if (live is not null)
            {
                snapshot = await ConversationClassificationRunner.ReadSnapshotAsync(db, live, includeKnownValues: true, cancellationToken);
            }
        }

        // The live conversation decides the sandbox flag of the usage (never a stale copy).
        var context = new ClassificationContext(request.TenantId, request.ConversationId, live?.IsSandbox ?? request.IsSandbox, null);
        var started = Stopwatch.GetTimestamp();
        var prepared = snapshot is null ? null : decide.Prepare(snapshot);
        if (prepared is null || !string.Equals(prepared.SnapshotHash, request.SnapshotHash, StringComparison.Ordinal))
        {
            // Gone (suppressed) or changed after the batch was sent: the answer was paid for but is not about it any more. Its cost is
            // recorded as an unusable call (never in the distinct set: a suppressed conversation leaves no id behind, design D24) and the
            // conversation is read again, once, synchronously while the window is still open.
            await decide.RecordUnappliedBatchAnswerAsync(context, result.Completion.Usage, Llm.BatchDiscountFactor);
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await ResolveAsync(db, request.Id, LlmBatchRequestStatus.Discarded, cancellationToken);
            if (live is not null)
            {
                await ReleaseLeaseAsync(db, request.ConversationId, request.LeaseUntil, cancellationToken);
            }

            await RecordAsync(work, UtcNow(), RunCounter.Discarded);
            LogBatchDiscard(request);
            return true;
        }

        var decision = decide.InterpretBatch(prepared, decide.ReadRules(snapshot!, cancellationToken), result.Completion, UtcNow(), Llm.BatchDiscountFactor);
        var outcome = await RelockAndApplyAsync(
            request.ConversationId, request.TenantId, request.RequestedAt, request.StageAtPlan, request.StageChangedAtPlan, null, request.SnapshotHash, request.LeaseUntil,
            decide, decision, cancellationToken);
        if (outcome.Applied is not { } counter)
        {
            // Still locked: back to Pending, nothing recorded yet, collected on a later poll.
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().LlmBatchRequests.IgnoreQueryFilters()
                .Where(r => r.Id == request.Id && r.Status == LlmBatchRequestStatus.Collecting)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, LlmBatchRequestStatus.Pending).SetProperty(r => r.CollectingAt, (DateTime?)null), cancellationToken);
            return false;
        }

        // Recorded once, now that the request is ours and its fate known: an applied reading as a success, anything else as a failed call.
        var applied = counter == RunCounter.Analysed;
        await decide.RecordBatchUsageAsync(context with { IsSandbox = outcome.IsSandbox }, decision, applied);
        var status = counter switch
        {
            RunCounter.Analysed => LlmBatchRequestStatus.Applied,
            RunCounter.Discarded => LlmBatchRequestStatus.Discarded,
            _ => LlmBatchRequestStatus.Failed,
        };
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            await ResolveAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), request.Id, status, cancellationToken);
        }

        await RecordAsync(work, UtcNow(), counter);
        LogCall(request.ConversationId, request.TenantId, outcome.IsSandbox, AiAnalysisMode.Nightly, batched: true, decision, counter, 0, 0,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (outcome.Profile is not null && outcome.ContactId is { } contactId)
        {
            await WriteProfileAsync(request.ConversationId, contactId, request.TenantId, outcome.Profile, UtcNow(), cancellationToken);
        }

        return true;
    }

    /// <summary>Takes a request for this collector: Pending → Collecting, atomically. False: another collector has it, or it is resolved.</summary>
    private async Task<bool> TakeAsync(ApplicationDbContext db, Guid requestId, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        return await db.LlmBatchRequests.IgnoreQueryFilters()
            .Where(r => r.Id == requestId && r.Status == LlmBatchRequestStatus.Pending)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, LlmBatchRequestStatus.Collecting).SetProperty(r => r.CollectingAt, now), cancellationToken) == 1;
    }

    private async Task ResolveAsync(ApplicationDbContext db, Guid requestId, LlmBatchRequestStatus status, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        await db.LlmBatchRequests.IgnoreQueryFilters().Where(r => r.Id == requestId)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, status).SetProperty(r => r.ResolvedAt, now), cancellationToken);
    }

    /// <summary>Releases a conversation's lease only while it still holds this one (another worker's newer lease is never cut).</summary>
    private static async Task ReleaseLeaseAsync(ApplicationDbContext db, Guid conversationId, DateTime? lease, CancellationToken cancellationToken)
        => await db.Conversations.IgnoreQueryFilters().Where(c => c.Id == conversationId && c.LlmLeaseUntil == lease)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.LlmLeaseUntil, (DateTime?)null), cancellationToken);

    // ---- Batch mode: retention -----------------------------------------------------------------------------------------

    /// <summary>
    /// Deletes resolved batches (collected or failed, with their requests) older than <c>Classification:Llm:Batch:RetentionDays</c> (30):
    /// they only steer the collection; the costs stay in <c>AiUsageMonths</c>. Called by the usage retention service. Returns the batches deleted.
    /// </summary>
    public async Task<int> PurgeResolvedBatchesAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var before = UtcNow().AddDays(-Llm.BatchRetentionDays);
        var old = db.LlmBatches.Where(b => (b.Status == LlmBatchStatus.Collected || b.Status == LlmBatchStatus.Failed) && b.CollectedAt != null && b.CollectedAt < before);
        await db.LlmBatchRequests.IgnoreQueryFilters().Where(r => old.Any(b => b.Id == r.BatchId)).ExecuteDeleteAsync(cancellationToken);
        return await old.ExecuteDeleteAsync(cancellationToken);
    }

    // ---- The contact profile (design D5): after the commit, Contact row only ------------------------------------------------

    /// <summary>
    /// Writes the profile the LLM read AFTER the commit, in a short transaction of its own that takes only the Contact row (design D5: ingestion
    /// takes Contact then Conversation). A manual value is never overwritten and nothing is erased. Then, in another short Conversation-only
    /// write, the pending marker is cleared (only if it still holds these values). If the write fails, the marker stays and
    /// <see cref="WritePendingProfileAsync"/> retries it after the retry delay, without calling the model again. Never throws, except when the
    /// host is stopping.
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
                await ConversationClassificationRunner.SetTimeoutsAsync(db, options.ProcessingTimeoutSeconds, budget.Token);
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
    /// Writes one profile left pending by a failed write or a crash: claims a conversation with the marker (not pending for the LLM, not
    /// leased, last attempt older than the retry delay, <c>FOR UPDATE SKIP LOCKED</c>), records the attempt time as a lease and commits (the
    /// Conversation lock is released before the Contact is taken), then writes the profile. The model is not called. Returns whether one was handled.
    /// </summary>
    public async Task<bool> WritePendingProfileAsync(CancellationToken cancellationToken = default)
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
            await ConversationClassificationRunner.SetTimeoutsAsync(db, options.ProcessingTimeoutSeconds, budget.Token);

            var retryFrom = now - options.RetryDelay;
            var active = nameof(TenantStatus.Active);
            var pending = (await db.Conversations
                .FromSql($"""
                    SELECT * FROM "Conversations"
                    WHERE "ProfilePending" AND NOT "LlmPending"
                      AND EXISTS (SELECT 1 FROM "Tenants" WHERE "Tenants"."Id" = "Conversations"."TenantId" AND "Tenants"."Status" = {active})
                      AND ("LlmLeaseUntil" IS NULL OR "LlmLeaseUntil" <= {now})
                      AND ("LlmAttemptedAt" IS NULL OR "LlmAttemptedAt" <= {retryFrom})
                    ORDER BY "LlmAttemptedAt" NULLS FIRST
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
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.LlmAttemptedAt, now), budget.Token);
            await transaction.CommitAsync(budget.Token);
        }

        await WriteProfileAsync(conversationId, contactId, tenantId, profile, now, cancellationToken);
        return true;
    }

    // ---- Run statistics (AiAnalysisRuns): counts per company and window, never text ----------------------------------------

    internal enum RunCounter
    {
        Claimed,
        Analysed,
        Unchanged,
        Skipped,
        Discarded,
        Failed,
        Deferred,
        Batched,
        Calls,
    }

    private async Task RecordAsync(TenantWork work, DateTime now, RunCounter counter)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var column = counter.ToString(); // a fixed enum name, never input
            var mode = work.Mode.ToString();
#pragma warning disable EF1002 // the column name is one of the fixed RunCounter names; every value is a parameter
            await db.Database.ExecuteSqlRawAsync(
                $"""
                INSERT INTO "AiAnalysisRuns" ("TenantId", "WindowStart", "WindowEnd", "Mode", "StartedAt", "LastActivityAt",
                    "Claimed", "Analysed", "Unchanged", "Skipped", "Discarded", "Failed", "Deferred", "Batched", "Calls")
                VALUES (@tenant, @start, @end, @mode, @now, @now, 0, 0, 0, 0, 0, 0, 0, 0, 0)
                ON CONFLICT ("TenantId", "WindowStart") DO NOTHING;
                UPDATE "AiAnalysisRuns" SET "{column}" = "{column}" + 1, "LastActivityAt" = @now WHERE "TenantId" = @tenant AND "WindowStart" = @start;
                """,
                [
                    new Npgsql.NpgsqlParameter("tenant", work.TenantId),
                    new Npgsql.NpgsqlParameter("start", work.Window.Start),
                    new Npgsql.NpgsqlParameter("end", work.Window.End),
                    new Npgsql.NpgsqlParameter("mode", mode),
                    new Npgsql.NpgsqlParameter("now", now),
                ],
                CancellationToken.None);
#pragma warning restore EF1002
        }
        catch (Exception ex)
        {
            // Statistics only: never stop an analysis for them.
            logger.LogWarning("Could not record the analysis statistics of tenant {TenantId}: {Error}", work.TenantId, ClassificationErrors.Describe(ex));
        }
    }

    // ---- Logs (ids, counts, codes and money; never text) ----------------------------------------------------------------

    private void LogWithoutCall(Conversation conversation, ClassificationDecision decision, long claimMs)
    {
        if (decision.Reason == ClassificationCoordinator.RealClientSandboxOnly)
        {
            logger.LogInformation(
                "Conversation {ConversationId} of tenant {TenantId} is not sandbox, so it was not sent to the real LLM client ({Reason}; set Classification:Llm:AllowRealClientNonSandbox after the privacy review): the rules read it.",
                conversation.Id, conversation.TenantId, decision.Reason);
            return;
        }

        logger.LogDebug(
            "LLM analysis without a call for conversation {ConversationId} of tenant {TenantId} (Sandbox {Sandbox}): {Completion}, {Reason}, claim transaction {ClaimTransactionMs} ms.",
            conversation.Id, conversation.TenantId, conversation.IsSandbox, decision.Completion, decision.Reason, claimMs);
    }

    private void LogBatchDiscard(LlmBatchRequest request)
        => logger.LogInformation(
            "Batch answer for conversation {ConversationId} of tenant {TenantId} discarded: the conversation changed after the batch was sent; it is analysed in the next window.",
            request.ConversationId, request.TenantId);

    /// <summary>
    /// One Information line per call, starting with <c>LLM classification</c>: ids, mode, whether it came from a batch, the call report
    /// (status, tokens, estimated USD with the batch discount when batched) and the preprocessor statistics, plus how long the two short
    /// transactions held the row (<c>ClaimTransactionMs</c>, <c>ApplyTransactionMs</c>) and the call time outside any lock (<c>CallMs</c>).
    /// A transaction longer than <c>Classification:Llm:ClaimWarningMilliseconds</c> is a Warning.
    /// </summary>
    private void LogCall(
        Guid conversationId, Guid tenantId, bool sandbox, AiAnalysisMode mode, bool batched, ClassificationDecision decision, RunCounter? applied, long claimMs, long callMs,
        long applyMs)
    {
        if (decision.Call is not { } call)
        {
            return;
        }

        var stats = call.Stats;
        var outcome = applied?.ToString().ToLowerInvariant() ?? "left_locked";
        logger.LogInformation(
            "LLM classification of conversation {ConversationId} of tenant {TenantId} (Sandbox {Sandbox}): mode {Mode}, batch {Batch}, provider {Provider}, model {Model}, " +
            "status {Status}, completion {Completion}, outcome {Outcome}, error {Error}, tokens in {InputTokens} out {OutputTokens} cache read {CacheReadTokens} " +
            "cache write {CacheWriteTokens}, estimated USD {EstimatedUsd}, {LatencyMs} ms, claim transaction {ClaimTransactionMs} ms, call {CallMs} ms, " +
            "apply transaction {ApplyTransactionMs} ms, won downgraded {WonDowngraded}; preprocessing: messages {MessagesIn}, characters {CharactersBefore} -> " +
            "{CharactersAfter}, estimated tokens {TokensBefore} -> {TokensAfter}, dropped {Dropped}, collapsed {Collapsed}, deduplicated {Deduplicated}, " +
            "truncated {Truncated}, merged {Merged}, omitted by budget {OmittedByBudget}.",
            conversationId, tenantId, sandbox, mode, batched, ClassificationOptions.LlmProvider, call.ModelId, call.Status, decision.Completion, outcome, call.Error,
            call.Usage.InputTokens, call.Usage.OutputTokens, call.Usage.CacheReadTokens, call.Usage.CacheWriteTokens, call.EstimatedUsd, call.LatencyMs, claimMs, callMs,
            applyMs, call.WonDowngraded, stats.MessagesIn, stats.CharactersBefore, stats.CharactersAfter, stats.EstimatedTokensBefore, stats.EstimatedTokensAfter,
            stats.MessagesDropped, stats.MessagesCollapsed, stats.MessagesDeduplicated, stats.MessagesTruncated, stats.MessagesMerged, stats.MessagesOmittedByBudget);

        var longest = Math.Max(claimMs, applyMs);
        if (longest > Llm.ClaimWarningMilliseconds)
        {
            logger.LogWarning(
                "An LLM analysis transaction held conversation {ConversationId} of tenant {TenantId} for {TransactionMs} ms (threshold {ClaimWarningMilliseconds} ms); the call itself holds no lock.",
                conversationId, tenantId, longest, Llm.ClaimWarningMilliseconds);
        }
    }

    private DateTime UtcNow() => DatabaseTime.Truncate(timeProvider.GetUtcNow().UtcDateTime);
}

/// <summary>
/// One process per batch (review: several API instances poll and recover the same table): a session-level PostgreSQL advisory lock on the
/// batch id, held on a connection of its own while the batch is polled, collected or recovered, released on dispose.
/// </summary>
internal sealed class BatchLock : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    private readonly ApplicationDbContext _db;
    private readonly string _key;

    private BatchLock(AsyncServiceScope scope, ApplicationDbContext db, string key)
    {
        _scope = scope;
        _db = db;
        _key = key;
    }

    public static async Task<BatchLock?> TryTakeAsync(IServiceScopeFactory scopes, Guid batchId, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var key = "conversia.llm-batch:" + batchId.ToString("N");
        await db.Database.OpenConnectionAsync(cancellationToken);
        var taken = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock(hashtextextended({key}, 0)) AS \"Value\"").SingleAsync(cancellationToken);
        if (taken)
        {
            return new BatchLock(scope, db, key);
        }

        await db.Database.CloseConnectionAsync();
        await scope.DisposeAsync();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _db.Database.SqlQuery<bool>($"SELECT pg_advisory_unlock(hashtextextended({_key}, 0)) AS \"Value\"").SingleAsync(CancellationToken.None);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
            await _scope.DisposeAsync();
        }
    }
}
