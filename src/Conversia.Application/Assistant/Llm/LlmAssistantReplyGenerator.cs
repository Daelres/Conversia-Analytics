using System.Diagnostics;
using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Conversia.Application.Assistant.Llm;

/// <summary>The assistant's two model clients: the reply (its own token ceiling) and the guard (small, no thinking).</summary>
public sealed record AssistantLlmClients(ILlmClient Reply, ILlmClient Guard);

/// <param name="GuardEnabled">Screen the latest turn with the guard call (<c>Assistant:Llm:Guard:Enabled</c>).</param>
/// <param name="KnowledgeTokenBudget">The company block's budget; above it the catalog is cut to what the conversation is about.</param>
/// <param name="GuardHandoffConfidence">From this guard confidence a request for a person or a complaint hands off (<c>Assistant:Llm:Guard:HandoffConfidence</c>).</param>
public sealed record AssistantLlmSettings(bool GuardEnabled, int KnowledgeTokenBudget, LlmPricing Pricing, TimeZoneInfo TimeZone, double GuardHandoffConfidence = AssistantGuard.DefaultHandoffConfidence);

/// <summary>
/// The model behind the 5a port (docs/ASSISTANT.md, "The model"): guard screen, one structured reply call, deterministic checks, one
/// regeneration with a corrective note, then a handoff. It never throws for the model (a failure is a handoff with the cost of what was
/// called), never sends the customer's name or numbers (the request is pseudonymized; <c>{nombre}</c> is filled here), and logs one line per
/// generation with ids, labels, codes, tokens and USD, never text.
/// </summary>
public sealed class LlmAssistantReplyGenerator(
    AssistantLlmClients clients, AssistantPromptBuilder builder, LlmCircuitBreaker breaker, AssistantLlmSettings settings,
    ILogger<LlmAssistantReplyGenerator> logger, TimeProvider? clock = null) : IAssistantReplyGenerator
{
    public static readonly EventId AssistantGenerated = new(4405, nameof(AssistantGenerated));

    public const int MaxReplyAttempts = 2;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private sealed class Meter(LlmPricing pricing)
    {
        public LlmUsage Usage { get; private set; } = LlmUsage.None;

        public int Calls { get; private set; }

        public int GuardCalls { get; private set; }

        public int FailedCalls { get; private set; }

        public string? Model { get; private set; }

        public void Add(LlmUsage usage, string? model, bool guard, bool failed)
        {
            Usage = new LlmUsage(Usage.InputTokens + usage.InputTokens, Usage.OutputTokens + usage.OutputTokens, Usage.CacheReadTokens + usage.CacheReadTokens,
                Usage.CacheWriteTokens + usage.CacheWriteTokens);
            Calls++;
            GuardCalls += guard ? 1 : 0;
            FailedCalls += failed ? 1 : 0;
            Model ??= model;
        }

        public AssistantUsage? ToUsage() => Calls == 0 ? null : new AssistantUsage(Model ?? "unknown", (int)Usage.InputTokens, (int)Usage.OutputTokens, pricing.Estimate(Usage))
        {
            CacheReadTokens = Usage.CacheReadTokens,
            CacheWriteTokens = Usage.CacheWriteTokens,
            Calls = Calls,
            GuardCalls = GuardCalls,
            FailedCalls = FailedCalls,
        };
    }

    private sealed record Outcome(AssistantReplyResult Result, string Guard, string Validation, int Attempts);

    public async Task<AssistantReplyResult> GenerateAsync(AssistantReplyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        var meter = new Meter(settings.Pricing);
        var outcome = await RunAsync(request, meter, cancellationToken);
        var result = outcome.Result with { Usage = meter.ToUsage() };
        var usage = meter.Usage;
        logger.LogInformation(AssistantGenerated,
            "Assistant generation for conversation {ConversationId} of tenant {TenantId} (Sandbox {Sandbox}): result {Result}, intent {Intent}, guard {Guard}, validation {Validation}, attempts {Attempts}, calls {Calls}, guard calls {GuardCalls}, failed {FailedCalls}, tokens in {InputTokens} out {OutputTokens} cache read {CacheReadTokens} cache write {CacheWriteTokens}, estimated USD {EstimatedUsd}, {ElapsedMs} ms.",
            request.ConversationId, request.TenantId, request.IsSandbox, Code(result), (result as AssistantReplyResult.Reply)?.Intent, outcome.Guard, outcome.Validation,
            outcome.Attempts, meter.Calls, meter.GuardCalls, meter.FailedCalls, usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens,
            result.Usage?.EstimatedUsd ?? 0m, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    private static string Code(AssistantReplyResult result) => result switch
    {
        AssistantReplyResult.Reply { HandoffAfterSend: { } after } => "reply_then_" + after,
        AssistantReplyResult.Reply => "reply",
        AssistantReplyResult.Handoff handoff => "handoff_" + handoff.Reason,
        AssistantReplyResult.NoReply noReply => "no_reply_" + noReply.Reason,
        _ => "unknown",
    };

    private async Task<Outcome> RunAsync(AssistantReplyRequest request, Meter meter, CancellationToken cancellationToken)
    {
        if (request.Knowledge is not { } knowledge)
        {
            // Nothing published: the assistant would have nothing true to say. A person answers (the customer is not told anything).
            return new Outcome(new AssistantReplyResult.Handoff(AssistantHandoffReasons.AssistantUnavailable, null), "none", "no_knowledge", 0);
        }

        var messages = request.Conversation.Messages;
        var turn = UnansweredCustomerTexts(messages);
        if (turn.Count == 0)
        {
            return new Outcome(new AssistantReplyResult.NoReply("nothing_to_answer"), "none", "none", 0);
        }

        var recent = messages.Where(m => m.Direction == MessageDirection.Inbound && !string.IsNullOrWhiteSpace(m.Text)).TakeLast(6).Select(m => m.Text!).ToList();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(request.UtcNow, DateTimeKind.Utc), settings.TimeZone);

        // 1. The guard (unless the turn is plainly a greeting or a catalog question).
        var mode = AssistantReplyMode.Answer;
        var guardLabel = "skipped";
        var lastBusiness = messages.LastOrDefault(m => m.Direction == MessageDirection.Outbound && !string.IsNullOrWhiteSpace(m.Text))?.Text;
        if (settings.GuardEnabled && !AssistantGuard.CanSkip(turn, knowledge))
        {
            var (completion, failure) = await CallAsync(clients.Guard, AssistantGuard.Build(turn, lastBusiness, knowledge), meter, guard: true, cancellationToken);
            var verdict = completion is { StopReason: LlmStopReason.EndTurn } ? AssistantGuard.Parse(completion.JsonText) : null;
            if (failure is not null || verdict is null)
            {
                // Fail closed: without a screen the turn is not answered by the model.
                var code = failure ?? (completion?.StopReason == LlmStopReason.Refusal ? "refused" : "invalid");
                var reason = completion?.StopReason == LlmStopReason.Refusal ? AssistantHandoffReasons.ModelRefused : AssistantHandoffReasons.GeneratorFailed;
                return new Outcome(new AssistantReplyResult.Handoff(reason, string.Empty), "failed_" + code, "none", 0);
            }

            guardLabel = verdict.Label.ToString();
            var route = AssistantGuard.Route(verdict, request.OffTopicCount, turn, settings.GuardHandoffConfidence);
            switch (route.Kind)
            {
                case GuardRouteKind.Handoff:
                    return new Outcome(new AssistantReplyResult.Handoff(route.HandoffReason!, string.Empty), guardLabel, "none", 0);
                case GuardRouteKind.NotUnderstood:
                    return new Outcome(new AssistantReplyResult.NoReply(AssistantReplyResult.NotUnderstood), guardLabel, "none", 0);
                case GuardRouteKind.Redirect:
                    mode = AssistantReplyMode.Redirect;
                    break;
            }
        }

        // 2. The reply, checked; one regeneration with a corrective note.
        var selection = AssistantKnowledgeSelector.Select(knowledge, request.BranchId, localNow, recent, settings.KnowledgeTokenBudget);
        var context = new ReplyCheckContext(selection, knowledge, recent, lastBusiness) { Today = DateOnly.FromDateTime(localNow) };
        string? note = null;
        var problems = (IReadOnlyList<string>)[];
        for (var attempt = 1; attempt <= MaxReplyAttempts; attempt++)
        {
            var prompt = builder.Build(new AssistantPromptInput(request.Conversation, selection, knowledge, request.Settings, localNow, request.MustDisclose, mode, note));
            var (completion, failure) = await CallAsync(clients.Reply, prompt, meter, guard: false, cancellationToken);
            if (failure is not null)
            {
                return new Outcome(new AssistantReplyResult.Handoff(AssistantHandoffReasons.GeneratorFailed, string.Empty), guardLabel, "failed_" + failure, attempt);
            }

            if (completion!.StopReason == LlmStopReason.Refusal)
            {
                return new Outcome(new AssistantReplyResult.Handoff(AssistantHandoffReasons.ModelRefused, string.Empty), guardLabel, "refused", attempt);
            }

            var answer = completion.StopReason == LlmStopReason.EndTurn ? AssistantModelAnswer.Parse(completion.JsonText) : null;
            if (answer is null)
            {
                problems = ["format"];
                note = AssistantReplyValidator.CorrectiveNote(problems);
                continue;
            }

            if (answer.Misunderstood)
            {
                return new Outcome(new AssistantReplyResult.NoReply(AssistantReplyResult.NotUnderstood), guardLabel, "misunderstood", attempt);
            }

            // Checked BEFORE the name is filled: the saved name is plain text, never read as a claim, a link or a marker.
            var text = CustomerNamePlaceholder.Normalize(answer.ReplyText.Trim());
            if (answer.WantsHandoff)
            {
                // The model's own line when it passes every check; otherwise the company's line.
                var line = AssistantReplyValidator.Check(answer with { Intent = "handoff" }, text, context).Count == 0 && text.Length > 0
                    ? CustomerNamePlaceholder.Fill(text, request.CustomerFirstName) : string.Empty;
                return new Outcome(new AssistantReplyResult.Handoff(HandoffReasonOf(answer.HandoffReason), line), guardLabel, "handoff", attempt);
            }

            problems = AssistantReplyValidator.Check(answer, text, context);
            if (problems.Count > 0)
            {
                logger.LogInformation(AssistantGenerated, "Assistant reply attempt {Attempt} of conversation {ConversationId} failed the checks: {Problems}.",
                    attempt, request.ConversationId, string.Join(", ", problems.Select(p => p.Split(':')[0]).Distinct()));
                note = AssistantReplyValidator.CorrectiveNote(problems);
                continue;
            }

            var closed = answer.SaleClosed && SaleEvidence.CustomerCommitted(turn, lastBusiness) && SaleEvidence.HasClosingPath(knowledge);
            var intent = mode == AssistantReplyMode.Redirect ? AssistantReplyResult.RedirectIntent : answer.Intent;
            return new Outcome(new AssistantReplyResult.Reply(CustomerNamePlaceholder.Fill(text, request.CustomerFirstName), answer.UsedKnowledgeIds, intent) { HandoffAfterSend = closed ? AssistantHandoffReasons.SaleClosed : null },
                guardLabel, answer.SaleClosed && !closed ? "passed_sale_closed_ignored" : "passed", attempt);
        }

        return new Outcome(new AssistantReplyResult.Handoff(AssistantHandoffReasons.InvalidReply, string.Empty), guardLabel,
            "invalid_" + string.Join("+", problems.Select(p => p.Split(':')[0]).Distinct()), MaxReplyAttempts);
    }

    private static string HandoffReasonOf(string? reason) => reason switch
    {
        "customer_wants_human" => AssistantHandoffReasons.HumanRequested,
        "complaint" => AssistantHandoffReasons.Complaint,
        "out_of_scope" => AssistantHandoffReasons.OffTopic,
        _ => AssistantHandoffReasons.GeneratorHandoff,
    };

    /// <summary>One call through the breaker. Returns the completion or the failure code; the usage is metered either way.</summary>
    private async Task<(LlmCompletion? Completion, string? Failure)> CallAsync(ILlmClient client, LlmRequest request, Meter meter, bool guard, CancellationToken cancellationToken)
    {
        var ticket = breaker.TryEnter(_clock.GetUtcNow().UtcDateTime);
        if (!ticket.Admitted)
        {
            return (null, "breaker_open");
        }

        try
        {
            var completion = await client.CompleteAsync(request, cancellationToken);
            var usable = completion.StopReason == LlmStopReason.EndTurn;
            meter.Add(completion.Usage, completion.ModelId, guard, failed: !usable);
            if (usable)
            {
                breaker.RecordSuccess(ticket);
            }
            else
            {
                breaker.Abandon(ticket);
            }

            return (completion, null);
        }
        catch (LlmCallException ex)
        {
            // A timeout or an outage may have been processed: its estimated input is counted, like the analysis (docs/CLASSIFICATION.md).
            var estimated = ex.Kind is LlmFailureKind.Timeout or LlmFailureKind.Unavailable
                ? new LlmUsage(PromptFormat.EstimateTokens(request.SystemPrompt.Length + request.ContextBlock.Length + request.UserContent.Length), 0, 0, 0)
                : LlmUsage.None;
            meter.Add(estimated, null, guard, failed: true);
            var now = _clock.GetUtcNow().UtcDateTime;
            if (ex.Kind == LlmFailureKind.Misconfigured)
            {
                breaker.RecordMisconfiguration(ticket, now, ex.ErrorType);
            }
            else if (ex.Kind.IsTransport())
            {
                breaker.RecordTransportFailure(ticket, now);
            }
            else
            {
                breaker.Abandon(ticket);
            }

            return (null, ex.Kind.ToString().ToLowerInvariant());
        }
        catch
        {
            breaker.Abandon(ticket);
            throw;
        }
    }

    /// <summary>The customer's messages after the business's last one (the turn to answer), oldest first.</summary>
    internal static IReadOnlyList<string> UnansweredCustomerTexts(IReadOnlyList<SnapshotMessage> messages)
    {
        var turn = new List<string>();
        for (var i = messages.Count - 1; i >= 0 && messages[i].Direction == MessageDirection.Inbound; i--)
        {
            if (!string.IsNullOrWhiteSpace(messages[i].Text))
            {
                turn.Insert(0, messages[i].Text!);
            }
        }

        return turn;
    }
}
