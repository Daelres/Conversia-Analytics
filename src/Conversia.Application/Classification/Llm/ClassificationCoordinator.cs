namespace Conversia.Application.Classification.Llm;

/// <param name="MaxFallbackRetries">Counted (content) failures allowed after the first attempt before the rules stand (<c>Llm:MaxFallbackRetries</c>).</param>
/// <param name="RetryDelay">The worker's retry delay (<c>Classification:RetryDelaySeconds</c>): a deferral is expressed as an attempt time this much earlier.</param>
/// <param name="IncludeSandbox">Whether sandbox conversations (docs/SANDBOX.md) go through the LLM (<c>Llm:IncludeSandbox</c>).</param>
/// <param name="ProbeDeferral">How long a conversation waits while another one is the breaker's half-open probe (30 s).</param>
/// <param name="MaxJitterSeconds">Random 0..this many seconds added to an open-breaker deferral, so a recovery does not stampede (30).</param>
/// <param name="SandboxOnly">Only sandbox conversations may reach the client (the real API before the privacy review: <c>Classification:Llm:AllowRealClientNonSandbox=false</c>).</param>
public sealed record LlmCoordinatorSettings(int MaxFallbackRetries, TimeSpan RetryDelay, bool IncludeSandbox, TimeSpan ProbeDeferral, int MaxJitterSeconds, LlmPricing Pricing, bool SandboxOnly = false);

/// <param name="LastLlmSnapshotHash">The hash of the LLM reading applied now (<c>Conversation.LastLlmSnapshotHash</c>), if any.</param>
public sealed record ClassificationContext(Guid TenantId, Guid ConversationId, bool IsSandbox, string? LastLlmSnapshotHash);

/// <summary>How the runner finishes the claimed conversation.</summary>
public enum ClassificationCompletion
{
    /// <summary>Apply <see cref="ClassificationDecision.Applied"/> (a rules reading) and clear the flag.</summary>
    Rules,

    /// <summary>Apply the guarded LLM reading, clear the flag, reset the LLM failures, store the hash, then write the contact profile.</summary>
    Llm,

    /// <summary>Apply the rules reading as an explicit fallback (LLM-sourced fields protected) and keep the flag per the counting rules.</summary>
    RulesFallback,

    /// <summary>Nothing the model reads changed since the applied LLM reading: apply nothing, clear the flag.</summary>
    Unchanged,
}

/// <summary>One call to the model, for the structured log line (ids are added by the runner). Counts, codes and money only, never text.</summary>
/// <param name="Status"><c>succeeded</c>, or the failure kind in snake case (<c>timeout</c>, <c>refused</c>, <c>invalid_output</c>, ...).</param>
public sealed record LlmCallReport(
    string ModelId, string Status, LlmUsage Usage, decimal EstimatedUsd, long LatencyMs, PreprocessingStats Stats, bool WonDowngraded, bool WonDisagreement, string? Error);

/// <param name="Applied">What to write to the opportunity; null for <see cref="ClassificationCompletion.Unchanged"/>.</param>
/// <param name="Reason">Why, as a short code (<c>llm</c>, <c>content_failure</c>, <c>transport_failure</c>, <c>breaker_open</c>,
/// <c>probe_in_flight</c>, <c>hash_unchanged</c>, <c>no_customer_text</c>, <c>sandbox_excluded</c>, <c>not_admitted:*</c>).</param>
/// <param name="SnapshotHash">With <see cref="ClassificationCompletion.Llm"/> and <see cref="ClassificationCompletion.Unchanged"/>: the hash to keep.</param>
/// <param name="CountAsLlmFailure">With <see cref="ClassificationCompletion.RulesFallback"/>: a content failure, counted in <c>LlmFailures</c>.</param>
/// <param name="AttemptedAt">With <see cref="ClassificationCompletion.RulesFallback"/>: the attempt time to record (later than now defers the next claim).</param>
/// <param name="Call">The call made, if one was.</param>
public sealed record ClassificationDecision(
    ClassificationCompletion Completion, ClassificationResult? Applied, string Reason, string? SnapshotHash, LlmFailureKind? FailureKind,
    bool CountAsLlmFailure, DateTime AttemptedAt, LlmCallReport? Call);

/// <summary>
/// Phase 1 of the three-phase runner (design H1): either a decision that needs no call (<see cref="Resolved"/>), or a call to make outside
/// any lock (<see cref="Prepared"/>, with the rules reading of the same snapshot for the Won guard and the breaker ticket it holds).
/// </summary>
public sealed record LlmPlan(ClassificationDecision? Resolved, PreparedLlmCall? Prepared, ClassificationResult Rules, BreakerTicket Ticket)
{
    public bool NeedsCall => Resolved is null;
}

/// <summary>
/// Decides how one claimed conversation is read when <c>Classification:Provider=Llm</c> (design D1, D12, D21; Stage A). Pure application
/// logic: no database, no clock of its own (the runner passes <c>now</c>). In order:
/// <list type="number">
/// <item>The rules read the REAL snapshot, always: they are the fallback and the Won guard's reference.</item>
/// <item>A sandbox conversation goes to the plain rules unless <see cref="LlmCoordinatorSettings.IncludeSandbox"/>.</item>
/// <item>Tenant admission (<see cref="IAiUsageMeter"/>: the AI switch, the deployment cap, the company's quota and USD ceiling): not admitted =
/// the rules, flag cleared, LLM-sourced fields protected.</item>
/// <item>Call reduction: no customer text after preprocessing = the plain rules, no call; the same snapshot hash as the applied LLM
/// reading = <see cref="ClassificationCompletion.Unchanged"/>, no call (nothing the model reads changed).</item>
/// <item>The breaker: open = the rules, uncounted, deferred to about the reopen time plus jitter; half-open with a probe in flight =
/// deferred <see cref="LlmCoordinatorSettings.ProbeDeferral"/>.</item>
/// <item>One call. Transport failure: rules, uncounted, retried after the retry delay, fed to the breaker. Content failure: rules, counted.
/// Success: the <see cref="WonGuard"/> over the rules reading, then the LLM reading. Every call is recorded by the meter (success or
/// failure, tokens and estimated cost) before the decision is returned.</item>
/// </list>
/// </summary>
public sealed class ClassificationCoordinator
{
    private readonly RuleBasedConversationClassifier _rules;
    private readonly LlmConversationClassifier _llm;
    private readonly LlmCircuitBreaker _breaker;
    private readonly IAiUsageMeter _meter;
    private readonly LlmCoordinatorSettings _settings;
    private readonly Random _random;
    private readonly object _randomGate = new();

    public ClassificationCoordinator(
        RuleBasedConversationClassifier rules, LlmConversationClassifier llm, LlmCircuitBreaker breaker, IAiUsageMeter meter, LlmCoordinatorSettings settings,
        Random random, TimeProvider? time = null)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The reason of a real (non-sandbox) conversation kept away from the real client (<see cref="LlmCoordinatorSettings.SandboxOnly"/>).</summary>
    public const string RealClientSandboxOnly = "real_client_sandbox_only";

    private readonly TimeProvider _time;

    // Microseconds, like PostgreSQL timestamps: the times the runner writes compare exactly.
    private static DateTime DatabasePrecision(DateTime value) => new(value.Ticks - (value.Ticks % 10), DateTimeKind.Utc);

    /// <param name="snapshot">The REAL snapshot, read with the known values.</param>
    /// <param name="now">The runner's time (database precision).</param>
    public async Task<ClassificationDecision> DecideAsync(ConversationSnapshot snapshot, ClassificationContext context, DateTime now, CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(snapshot, context, now, useBreaker: true, cancellationToken);
        return plan.Resolved ?? await ExecuteAsync(plan, context, now, cancellationToken);
    }

    /// <summary>
    /// Phase 1: everything up to the call, without making it (rules, sandbox rules, admission, call reduction, and, with
    /// <paramref name="useBreaker"/>, the breaker ticket). A batch request uses no ticket: nothing is called synchronously.
    /// </summary>
    public async Task<LlmPlan> PlanAsync(ConversationSnapshot snapshot, ClassificationContext context, DateTime now, bool useBreaker = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);

        var rules = _rules.Classify(snapshot, cancellationToken);
        LlmPlan Resolved(ClassificationDecision decision) => new(decision, null, rules, default);

        if (context.IsSandbox && !_settings.IncludeSandbox)
        {
            return Resolved(Plain(rules, "sandbox_excluded", now));
        }

        if (_settings.SandboxOnly && !context.IsSandbox)
        {
            return Resolved(Plain(rules, RealClientSandboxOnly, now));
        }

        var admission = await _meter.CheckAdmissionAsync(context.TenantId, context.IsSandbox, cancellationToken);
        if (admission != AiAdmission.Allowed)
        {
            return Resolved(new ClassificationDecision(
                ClassificationCompletion.Rules, rules with { IsRulesFallback = true }, "not_admitted:" + admission, null, null, false, now, null));
        }

        var prepared = _llm.Prepare(snapshot);
        if (!prepared.HasCustomerText)
        {
            return Resolved(Plain(rules, "no_customer_text", now));
        }

        if (string.Equals(prepared.SnapshotHash, context.LastLlmSnapshotHash, StringComparison.Ordinal))
        {
            return Resolved(new ClassificationDecision(ClassificationCompletion.Unchanged, null, "hash_unchanged", prepared.SnapshotHash, null, false, now, null));
        }

        if (!useBreaker)
        {
            return new LlmPlan(null, prepared, rules, new BreakerTicket(true, false, BreakerDenial.None, null));
        }

        var ticket = _breaker.TryEnter(now);
        if (!ticket.Admitted)
        {
            var attemptedAt = ticket.Denial == BreakerDenial.Open
                ? ticket.ReopensAt!.Value - _settings.RetryDelay + TimeSpan.FromSeconds(Jitter())
                : now + _settings.ProbeDeferral - _settings.RetryDelay;
            var reason = ticket.Denial == BreakerDenial.Open ? "breaker_open" : "probe_in_flight";
            return Resolved(Fallback(rules, reason, null, countAsLlmFailure: false, attemptedAt, null));
        }

        return new LlmPlan(null, prepared, rules, ticket);
    }

    /// <summary>Phase 2: the call of a plan that needs one, its usage recorded, the breaker told. Never holds a database lock.</summary>
    public async Task<ClassificationDecision> ExecuteAsync(LlmPlan plan, ClassificationContext context, DateTime now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        if (plan.Resolved is { } resolved)
        {
            return resolved;
        }

        var prepared = plan.Prepared!;
        var rules = plan.Rules;
        var ticket = plan.Ticket;
        LlmAttempt attempt;
        try
        {
            attempt = await _llm.CallAsync(prepared, cancellationToken);
        }
        catch
        {
            _breaker.Abandon(ticket);
            throw;
        }

        if (attempt.Failure is { } kind)
        {
            // The breaker gets the time the call ENDED: a call that took the whole timeout must not shorten the cooldown.
            var ended = DatabasePrecision(_time.GetUtcNow().UtcDateTime);
            var report = Report(attempt, prepared, StatusOf(kind), null, 1m);
            var billed = BilledUsage(kind, attempt.Usage, prepared);
            // The call reached the provider: its cost is recorded whatever happened to the caller's budget meanwhile, and the breaker learns
            // the outcome; only then is the caller's cancellation honoured.
            await _meter.RecordFailureAsync(
                new AiUsageRecord(context.TenantId, context.ConversationId, context.IsSandbox, billed, _settings.Pricing.Estimate(billed)), CancellationToken.None);
            ClassificationDecision failed;
            if (kind == LlmFailureKind.Misconfigured)
            {
                _breaker.RecordMisconfiguration(ticket, ended, attempt.Error ?? "misconfigured");
                failed = Fallback(rules, "misconfigured", kind, countAsLlmFailure: false, now, report);
            }
            else if (kind.IsTransport())
            {
                _breaker.RecordTransportFailure(ticket, ended);
                failed = Fallback(rules, "transport_failure", kind, countAsLlmFailure: false, now, report);
            }
            else
            {
                // An unusable answer says nothing about the transport: it neither resets nor feeds the breaker.
                _breaker.Abandon(ticket);
                failed = Fallback(rules, "content_failure", kind, countAsLlmFailure: true, now, report);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return failed;
        }

        _breaker.RecordSuccess(ticket);
        var guarded = WonGuard.Apply(attempt.Result!, rules);
        var succeeded = Report(attempt, prepared, "succeeded", guarded, 1m);
        await _meter.RecordSuccessAsync(
            new AiUsageRecord(context.TenantId, context.ConversationId, context.IsSandbox, attempt.Usage, succeeded.EstimatedUsd, guarded.WonDisagreement),
            CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        return new ClassificationDecision(ClassificationCompletion.Llm, guarded.Result, "llm", prepared.SnapshotHash, null, false, now, succeeded);
    }

    /// <summary>
    /// A batch answer that cannot be applied any more (the conversation changed after the batch was sent): it was billed, so its usage is
    /// recorded like an unusable answer (a call, no conversation analysed) at <paramref name="costFactor"/> of the list price.
    /// </summary>
    public Task RecordUnappliedBatchAnswerAsync(ClassificationContext context, LlmUsage usage, decimal costFactor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(usage);
        return _meter.RecordFailureAsync(
            new AiUsageRecord(context.TenantId, context.ConversationId, context.IsSandbox, usage, _settings.Pricing.Estimate(usage) * costFactor), CancellationToken.None);
    }

    /// <summary>What the model would be sent for this snapshot, and its hash (phase 3 compares it; a batch result is read against it).</summary>
    public PreparedLlmCall Prepare(ConversationSnapshot snapshot) => _llm.Prepare(snapshot);

    /// <summary>The rules reading of the REAL snapshot: the Won guard's reference for a batch answer.</summary>
    public ClassificationResult ReadRules(ConversationSnapshot snapshot, CancellationToken cancellationToken = default) => _rules.Classify(snapshot, cancellationToken);

    /// <summary>A plan that will not be executed (phase 1 failed to commit its lease): its breaker ticket is given back.</summary>
    public void Release(LlmPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.NeedsCall && plan.Ticket.Admitted)
        {
            _breaker.Abandon(plan.Ticket);
        }
    }

    /// <summary>
    /// A batch answer (design batch mode): read like a synchronous one (mapper, Won guard over <paramref name="rules"/>), its usage recorded at
    /// <paramref name="costFactor"/> of the list price (0.5: the Message Batches discount). The breaker is not involved.
    /// </summary>
    public async Task<ClassificationDecision> InterpretBatchAsync(
        PreparedLlmCall prepared, ClassificationResult rules, LlmCompletion completion, ClassificationContext context, DateTime now, decimal costFactor,
        CancellationToken cancellationToken = default)
    {
        var decision = InterpretBatch(prepared, rules, completion, now, costFactor);
        await RecordBatchUsageAsync(context, decision, applied: decision.Completion == ClassificationCompletion.Llm);
        cancellationToken.ThrowIfCancellationRequested();
        return decision;
    }

    /// <summary>A batch answer read like a synchronous one (mapper, Won guard), WITHOUT recording its usage: the collector records it once,
    /// after it knows whether the answer was applied (<see cref="RecordBatchUsageAsync"/>).</summary>
    public ClassificationDecision InterpretBatch(PreparedLlmCall prepared, ClassificationResult rules, LlmCompletion completion, DateTime now, decimal costFactor)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(completion);

        var attempt = _llm.Interpret(prepared, completion, latencyMs: 0);
        if (attempt.Failure is { } kind)
        {
            return Fallback(rules, "content_failure", kind, countAsLlmFailure: true, now, Report(attempt, prepared, StatusOf(kind), null, costFactor));
        }

        var guarded = WonGuard.Apply(attempt.Result!, rules);
        return new ClassificationDecision(
            ClassificationCompletion.Llm, guarded.Result, "llm", prepared.SnapshotHash, null, false, now, Report(attempt, prepared, "succeeded", guarded, costFactor));
    }

    /// <summary>
    /// Records a batch answer's usage once: an applied reading as a success (the conversation counts for the quota), anything else (an
    /// unusable answer, a reading thrown away in phase 3) as a failed call with its billed tokens and cost.
    /// </summary>
    public Task RecordBatchUsageAsync(ClassificationContext context, ClassificationDecision decision, bool applied)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Call is not { } call)
        {
            return Task.CompletedTask;
        }

        var record = new AiUsageRecord(context.TenantId, context.ConversationId, context.IsSandbox, call.Usage, call.EstimatedUsd, applied && call.WonDisagreement);
        return applied && decision.Completion == ClassificationCompletion.Llm
            ? _meter.RecordSuccessAsync(record, CancellationToken.None)
            : _meter.RecordFailureAsync(record, CancellationToken.None);
    }

    /// <summary>
    /// What a failed call may have cost (design D13): the usage the provider reported; when it reported none for a call that reached it and may
    /// have been processed (a timeout, a 5xx or a dropped connection), the estimated input of the prompt sent; a call refused at the door (429,
    /// 401/403/404) costs nothing.
    /// </summary>
    private static LlmUsage BilledUsage(LlmFailureKind kind, LlmUsage reported, PreparedLlmCall prepared)
    {
        if (reported != LlmUsage.None || kind is not (LlmFailureKind.Timeout or LlmFailureKind.Unavailable))
        {
            return reported;
        }

        var prompt = prepared.Prompt;
        var characters = prompt.SystemPrompt.Length + prompt.ContextBlock.Length + prompt.UserContent.Length;
        return new LlmUsage(PromptFormat.EstimateTokens(characters), 0, 0, 0);
    }

    private static ClassificationDecision Plain(ClassificationResult rules, string reason, DateTime now)
        => new(ClassificationCompletion.Rules, rules, reason, null, null, false, now, null);

    private static ClassificationDecision Fallback(
        ClassificationResult rules, string reason, LlmFailureKind? kind, bool countAsLlmFailure, DateTime attemptedAt, LlmCallReport? call)
        => new(ClassificationCompletion.RulesFallback, rules with { IsRulesFallback = true }, reason, null, kind, countAsLlmFailure, attemptedAt, call);

    private LlmCallReport Report(LlmAttempt attempt, PreparedLlmCall prepared, string status, WonGuardDecision? guard, decimal costFactor)
        => new(attempt.ModelId, status, attempt.Usage, _settings.Pricing.Estimate(attempt.Usage) * costFactor, attempt.LatencyMs, prepared.Stats,
            guard?.WonDowngraded ?? false, guard?.WonDisagreement ?? false, attempt.Error);

    private static string StatusOf(LlmFailureKind kind) => kind switch
    {
        LlmFailureKind.Timeout => "timeout",
        LlmFailureKind.RateLimited => "rate_limited",
        LlmFailureKind.Unavailable => "unavailable",
        LlmFailureKind.Refused => "refused",
        LlmFailureKind.Truncated => "truncated",
        LlmFailureKind.Misconfigured => "misconfigured",
        _ => "invalid_output",
    };

    private int Jitter()
    {
        lock (_randomGate)
        {
            return _random.Next(0, _settings.MaxJitterSeconds + 1);
        }
    }
}
