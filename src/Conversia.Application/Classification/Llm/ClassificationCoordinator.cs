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
/// Decides how one claimed conversation is read when <c>Classification:Provider=Llm</c> (design D1, D12, D21; Stage A). Pure application
/// logic: no database, no clock of its own (the runner passes <c>now</c>). In order:
/// <list type="number">
/// <item>The rules read the REAL snapshot, always: they are the fallback and the Won guard's reference.</item>
/// <item>A sandbox conversation goes to the plain rules unless <see cref="LlmCoordinatorSettings.IncludeSandbox"/>.</item>
/// <item>Tenant admission (<see cref="IAiUsageMeter"/>; Stage A admits everyone): not admitted = the rules, flag cleared, LLM-sourced fields protected.</item>
/// <item>Call reduction: no customer text after preprocessing = the plain rules, no call; the same snapshot hash as the applied LLM
/// reading = <see cref="ClassificationCompletion.Unchanged"/>, no call (nothing the model reads changed).</item>
/// <item>The breaker: open = the rules, uncounted, deferred to about the reopen time plus jitter; half-open with a probe in flight =
/// deferred <see cref="LlmCoordinatorSettings.ProbeDeferral"/>.</item>
/// <item>One call. Transport failure: rules, uncounted, retried after the retry delay, fed to the breaker. Content failure: rules, counted.
/// Success: the <see cref="WonGuard"/> over the rules reading, then the LLM reading.</item>
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
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);

        var rules = _rules.Classify(snapshot, cancellationToken);

        if (context.IsSandbox && !_settings.IncludeSandbox)
        {
            return Plain(rules, "sandbox_excluded", now);
        }

        if (_settings.SandboxOnly && !context.IsSandbox)
        {
            return Plain(rules, RealClientSandboxOnly, now);
        }

        var admission = await _meter.CheckAdmissionAsync(context.TenantId, cancellationToken);
        if (admission != AiAdmission.Allowed)
        {
            return new ClassificationDecision(
                ClassificationCompletion.Rules, rules with { IsRulesFallback = true }, "not_admitted:" + admission, null, null, false, now, null);
        }

        var prepared = _llm.Prepare(snapshot);
        if (!prepared.HasCustomerText)
        {
            return Plain(rules, "no_customer_text", now);
        }

        if (string.Equals(prepared.SnapshotHash, context.LastLlmSnapshotHash, StringComparison.Ordinal))
        {
            return new ClassificationDecision(ClassificationCompletion.Unchanged, null, "hash_unchanged", prepared.SnapshotHash, null, false, now, null);
        }

        var ticket = _breaker.TryEnter(now);
        if (!ticket.Admitted)
        {
            var attemptedAt = ticket.Denial == BreakerDenial.Open
                ? ticket.ReopensAt!.Value - _settings.RetryDelay + TimeSpan.FromSeconds(Jitter())
                : now + _settings.ProbeDeferral - _settings.RetryDelay;
            var reason = ticket.Denial == BreakerDenial.Open ? "breaker_open" : "probe_in_flight";
            return Fallback(rules, reason, null, countAsLlmFailure: false, attemptedAt, null);
        }

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
            var report = Report(attempt, prepared, StatusOf(kind), null);
            if (kind == LlmFailureKind.Misconfigured)
            {
                _breaker.RecordMisconfiguration(ticket, ended, attempt.Error ?? "misconfigured");
                return Fallback(rules, "misconfigured", kind, countAsLlmFailure: false, now, report);
            }

            if (kind.IsTransport())
            {
                _breaker.RecordTransportFailure(ticket, ended);
                return Fallback(rules, "transport_failure", kind, countAsLlmFailure: false, now, report);
            }

            // An unusable answer says nothing about the transport: it neither resets nor feeds the breaker.
            _breaker.Abandon(ticket);
            return Fallback(rules, "content_failure", kind, countAsLlmFailure: true, now, report);
        }

        _breaker.RecordSuccess(ticket);
        var guarded = WonGuard.Apply(attempt.Result!, rules);
        return new ClassificationDecision(
            ClassificationCompletion.Llm, guarded.Result, "llm", prepared.SnapshotHash, null, false, now, Report(attempt, prepared, "succeeded", guarded));
    }

    private static ClassificationDecision Plain(ClassificationResult rules, string reason, DateTime now)
        => new(ClassificationCompletion.Rules, rules, reason, null, null, false, now, null);

    private static ClassificationDecision Fallback(
        ClassificationResult rules, string reason, LlmFailureKind? kind, bool countAsLlmFailure, DateTime attemptedAt, LlmCallReport? call)
        => new(ClassificationCompletion.RulesFallback, rules with { IsRulesFallback = true }, reason, null, kind, countAsLlmFailure, attemptedAt, call);

    private LlmCallReport Report(LlmAttempt attempt, PreparedLlmCall prepared, string status, WonGuardDecision? guard)
        => new(attempt.ModelId, status, attempt.Usage, _settings.Pricing.Estimate(attempt.Usage), attempt.LatencyMs, prepared.Stats,
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
