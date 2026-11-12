using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Domain.Enums;
using Conversia.Tests.Shared;
using static Conversia.Application.UnitTests.Classification.Llm.LlmConversationClassifierTests;

namespace Conversia.Application.UnitTests.Classification.Llm;

/// <summary>
/// The coordinator matrix (design D1, D12, "Fallback and retry"; spec CC-1, CC-5, CC-6): rules always read on the real snapshot, the Won guard,
/// content failures counted, transport failures only toward the breaker, the breaker's deferrals, and the two call-reduction rules (an
/// unchanged snapshot hash and a conversation without customer text never call the model).
/// </summary>
public class ClassificationCoordinatorTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(60);
    private static readonly RuleBasedConversationClassifier RulesClassifier = new(Rules);

    private static readonly ConversationSnapshot Refused = ChatScript.Snapshot("""
        C: Hola, cuánto cuesta la depilación de axilas?
        A: Son $60.000 la sesión.
        C: Muy caro, no gracias
        """);

    private static readonly ConversationSnapshot Booked = ChatScript.Snapshot("""
        C: Quiero agendar axilas
        A: ¿Te agendo el sábado a las 3?
        C: dale
        A: Listo, quedó agendada tu cita para el sábado a las 3
        """);

    private sealed class Meter(AiAdmission admission) : IAiUsageMeter
    {
        public Task<AiAdmission> CheckAdmissionAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(admission);
    }

    private sealed record Setup(ClassificationCoordinator Coordinator, FakeLlmClient Client, LlmCircuitBreaker Breaker, FixedTimeProvider Clock);

    private static Setup New(
        Func<LlmRequest, LlmCompletion> answer, int breakerFailures = 5, bool includeSandbox = true, AiAdmission admission = AiAdmission.Allowed,
        int maxFallbackRetries = 2, bool sandboxOnly = false, FixedTimeProvider? clock = null)
    {
        var client = new FakeLlmClient(answer);
        var breaker = new LlmCircuitBreaker(breakerFailures, TimeSpan.FromSeconds(300));
        var settings = new LlmCoordinatorSettings(maxFallbackRetries, RetryDelay, includeSandbox, TimeSpan.FromSeconds(30), MaxJitterSeconds: 30, LlmPricing.Default, sandboxOnly);
        clock ??= new FixedTimeProvider(Now);
        var coordinator = new ClassificationCoordinator(RulesClassifier, LlmConversationClassifierTests.New(client), breaker, new Meter(admission), settings, new Random(7), clock);
        return new Setup(coordinator, client, breaker, clock);
    }

    private static ClassificationContext Context(bool sandbox = false, string? lastHash = null) => new(Guid.NewGuid(), Guid.NewGuid(), sandbox, lastHash);

    private static Func<LlmRequest, LlmCompletion> Throws(LlmFailureKind kind) => _ => throw new LlmCallException(kind, "FakeException");

    [Fact]
    public async Task AUsableAnswer_IsAppliedAsTheLlmReading_WithItsHash_AndOneCallReport()
    {
        var service = Catalog.Services[0];
        var setup = New(_ => Completed(Answer("lost", "price", "price", [service], gender: "female")));

        var decision = await setup.Coordinator.DecideAsync(Refused, Context(), Now);

        Assert.Equal(ClassificationCompletion.Llm, decision.Completion);
        Assert.Equal((ClassificationOutcome.Lost, (LossReason?)LossReason.Price, service, Gender.Female, false),
            (decision.Applied!.Outcome, decision.Applied.LossReason, decision.Applied.ServiceInterest, decision.Applied.Gender, decision.Applied.IsRulesFallback));
        Assert.Equal(setup.Client.Requests[0].SnapshotHash, decision.SnapshotHash);
        Assert.NotNull(decision.Call);
        Assert.Equal(("succeeded", 900L, 3000L), (decision.Call!.Status, decision.Call.Usage.InputTokens, decision.Call.Usage.CacheReadTokens));
        Assert.True(decision.Call.EstimatedUsd > 0m);
        Assert.Equal(3, decision.Call.Stats.MessagesIn);
    }

    [Fact]
    public async Task ALlmWon_TheRulesDidNotRead_IsDowngradedToTheRulesOutcome()
    {
        // CC-5 S13: the rules read Lost/Price; the model's Won becomes Lost/Price, guard code first.
        var setup = New(_ => Completed(Answer("won", wonLine: 2)));

        var decision = await setup.Coordinator.DecideAsync(Refused, Context(), Now);

        Assert.Equal(ClassificationCompletion.Llm, decision.Completion);
        Assert.Equal(ClassificationOutcome.Lost, decision.Applied!.Outcome);
        Assert.Equal(WonGuard.WonDowngradedCode, decision.Applied.RuleCodes[0]);
        Assert.True(decision.Call!.WonDowngraded);
    }

    [Fact]
    public async Task ARulesWon_StandsWhateverTheModelSaid()
    {
        var setup = New(_ => Completed(Answer("lost", "not_interested")));

        var decision = await setup.Coordinator.DecideAsync(Booked, Context(), Now);

        Assert.Equal(ClassificationOutcome.Won, decision.Applied!.Outcome);
        Assert.True(decision.Call!.WonDisagreement);
    }

    [Theory]
    [InlineData(LlmStopReason.Refusal, LlmFailureKind.Refused)]
    [InlineData(LlmStopReason.MaxTokens, LlmFailureKind.Truncated)]
    public async Task AContentFailure_AppliesTheRulesAsAFallback_AndIsCounted_ButNotByTheBreaker(LlmStopReason stop, LlmFailureKind kind)
    {
        var setup = New(_ => Completed(Answer(), stop), breakerFailures: 1);

        var decision = await setup.Coordinator.DecideAsync(Refused, Context(), Now);

        Assert.Equal((ClassificationCompletion.RulesFallback, (LlmFailureKind?)kind, true, Now), (decision.Completion, decision.FailureKind, decision.CountAsLlmFailure, decision.AttemptedAt));
        Assert.True(decision.Applied!.IsRulesFallback);
        Assert.Equal(RulesClassifier.Classify(Refused).Outcome, decision.Applied.Outcome);
        Assert.Null(decision.SnapshotHash);
        Assert.Equal(BreakerState.Closed, setup.Breaker.State(Now));
    }

    [Fact]
    public async Task AnInvalidAnswer_IsAContentFailureToo()
    {
        var setup = New(_ => Completed("{\"stage\":\"maybe\"}"));

        var decision = await setup.Coordinator.DecideAsync(Refused, Context(), Now);

        Assert.Equal((LlmFailureKind.InvalidOutput, true), (decision.FailureKind!.Value, decision.CountAsLlmFailure));
        Assert.Equal("invalid_output", decision.Call!.Status);
    }

    [Theory]
    [InlineData(LlmFailureKind.Timeout)]
    [InlineData(LlmFailureKind.RateLimited)]
    [InlineData(LlmFailureKind.Unavailable)]
    public async Task ATransportFailure_AppliesTheRulesUncounted_RetriesAfterTheRetryDelay_AndFeedsTheBreaker(LlmFailureKind kind)
    {
        // CC-6 S1/S3: rules applied, LlmFailures unchanged, still dirty, attempt time now (claimable again after RetryDelay).
        var setup = New(Throws(kind), breakerFailures: 2);

        var first = await setup.Coordinator.DecideAsync(Refused, Context(), Now);
        Assert.Equal(BreakerState.Closed, setup.Breaker.State(Now));
        await setup.Coordinator.DecideAsync(Refused, Context(), Now);

        Assert.Equal((ClassificationCompletion.RulesFallback, (LlmFailureKind?)kind, false, Now), (first.Completion, first.FailureKind, first.CountAsLlmFailure, first.AttemptedAt));
        Assert.True(first.Applied!.IsRulesFallback);
        Assert.Equal(BreakerState.Open, setup.Breaker.State(Now));
    }

    [Fact]
    public async Task TenConsecutiveContentFailures_LeaveTheBreakerClosed()
    {
        var setup = New(_ => Completed(null, LlmStopReason.Refusal), breakerFailures: 5);

        for (var i = 0; i < 10; i++)
        {
            await setup.Coordinator.DecideAsync(Refused, Context(), Now);
        }

        Assert.Equal(BreakerState.Closed, setup.Breaker.State(Now));
        Assert.Equal(10, setup.Client.Requests.Count);
    }

    [Fact]
    public async Task AnOpenBreaker_MakesNoCall_AndDefersTheConversationToAboutTheReopenTime_WithJitter()
    {
        // CC-6 S8: completed with the rules, uncounted, dirty, AttemptedAt = reopenAt - RetryDelay + jitter (0-30 s).
        var setup = New(Throws(LlmFailureKind.Unavailable), breakerFailures: 1);
        await setup.Coordinator.DecideAsync(Refused, Context(), Now);
        var reopen = Now.AddSeconds(300);
        var calls = setup.Client.Requests.Count;

        var decisions = new List<ClassificationDecision>();
        for (var i = 0; i < 20; i++)
        {
            decisions.Add(await setup.Coordinator.DecideAsync(Refused, Context(), Now.AddSeconds(10)));
        }

        Assert.Equal(calls, setup.Client.Requests.Count);
        Assert.All(decisions, d =>
        {
            Assert.Equal((ClassificationCompletion.RulesFallback, false, "breaker_open"), (d.Completion, d.CountAsLlmFailure, d.Reason));
            Assert.InRange(d.AttemptedAt, reopen - RetryDelay, reopen - RetryDelay + TimeSpan.FromSeconds(30));
            Assert.Null(d.Call);
        });
        Assert.True(decisions.Select(d => d.AttemptedAt).Distinct().Count() > 1, "the jitter spreads the deferred conversations");
    }

    [Fact]
    public async Task AfterTheCooldown_OneProbeIsCalled_AndTheOthersAreDeferredThirtySeconds()
    {
        // CC-6 S12 with an in-flight probe: the probe's call is held open while two more conversations come due.
        var release = new TaskCompletionSource();
        var probeStarted = new TaskCompletionSource();
        var breaker = new LlmCircuitBreaker(1, TimeSpan.FromSeconds(300));
        var calls = 0;
        var client = new BlockingClient(async () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new LlmCallException(LlmFailureKind.Timeout, "FakeException");
            }

            probeStarted.TrySetResult();
            await release.Task;
            return Completed(Answer());
        });
        var coordinator = new ClassificationCoordinator(RulesClassifier, LlmConversationClassifierTests.New(client), breaker, NullAiUsageMeter.Instance,
            new LlmCoordinatorSettings(2, RetryDelay, true, TimeSpan.FromSeconds(30), 30, LlmPricing.Default), new Random(1), new FixedTimeProvider(Now));
        await coordinator.DecideAsync(Refused, Context(), Now);
        var due = Now.AddSeconds(300);

        var probe = coordinator.DecideAsync(Refused, Context(), due);
        await probeStarted.Task;
        var second = await coordinator.DecideAsync(Refused, Context(), due);
        var third = await coordinator.DecideAsync(Refused, Context(), due);
        release.SetResult();
        var probed = await probe;

        Assert.Equal(2, calls);
        Assert.Equal(ClassificationCompletion.Llm, probed.Completion);
        foreach (var deferred in new[] { second, third })
        {
            Assert.Equal((ClassificationCompletion.RulesFallback, false, "probe_in_flight", due + TimeSpan.FromSeconds(30) - RetryDelay),
                (deferred.Completion, deferred.CountAsLlmFailure, deferred.Reason, deferred.AttemptedAt));
        }

        Assert.Equal(BreakerState.Closed, breaker.State(due));
    }

    private sealed class BlockingClient(Func<Task<LlmCompletion>> answer) : ILlmClient
    {
        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) => answer();
    }

    [Fact]
    public async Task AnUnchangedSnapshotHash_MakesNoCall_AndKeepsTheStoredReading()
    {
        var setup = New(_ => Completed(Answer()));
        var first = await setup.Coordinator.DecideAsync(Refused, Context(), Now);

        var again = await setup.Coordinator.DecideAsync(Refused, Context(lastHash: first.SnapshotHash), Now.AddMinutes(5));

        Assert.Single(setup.Client.Requests);
        Assert.Equal((ClassificationCompletion.Unchanged, (ClassificationResult?)null, first.SnapshotHash, (LlmCallReport?)null),
            (again.Completion, again.Applied, again.SnapshotHash, again.Call));
    }

    [Fact]
    public async Task AChangedSnapshot_IsCalledAgain()
    {
        var setup = New(_ => Completed(Answer()));
        var first = await setup.Coordinator.DecideAsync(Refused, Context(), Now);
        var longer = Refused with { Messages = [.. Refused.Messages, new SnapshotMessage(MessageDirection.Inbound, "text", "bueno, lo pienso", Now)] };

        var again = await setup.Coordinator.DecideAsync(longer, Context(lastHash: first.SnapshotHash), Now);

        Assert.Equal(2, setup.Client.Requests.Count);
        Assert.Equal(ClassificationCompletion.Llm, again.Completion);
        Assert.NotEqual(first.SnapshotHash, again.SnapshotHash);
    }

    [Theory]
    [InlineData("A: Hola, tenemos promoción de axilas\nA: ¿Te interesa?")]
    [InlineData("C: [audio]\nA: ¿Me escribes, por favor?")]
    public async Task NoCustomerText_MakesNoCall_AndFinishesWithThePlainRules(string script)
    {
        var setup = New(_ => Completed(Answer()));
        var snapshot = ChatScript.Snapshot(script);

        var decision = await setup.Coordinator.DecideAsync(snapshot, Context(), Now);

        Assert.Empty(setup.Client.Requests);
        Assert.Equal((ClassificationCompletion.Rules, false, false, "no_customer_text"),
            (decision.Completion, decision.Applied!.IsRulesFallback, decision.CountAsLlmFailure, decision.Reason));
        var rules = RulesClassifier.Classify(snapshot);
        Assert.Equal(rules with { RuleCodes = [] }, decision.Applied with { RuleCodes = [] });
        Assert.Equal(rules.RuleCodes, decision.Applied.RuleCodes);
    }

    [Fact]
    public async Task ASandboxConversation_GoesThroughTheLlm_WhenSandboxIsIncluded()
    {
        var setup = New(_ => Completed(Answer()), includeSandbox: true);

        var decision = await setup.Coordinator.DecideAsync(Refused, Context(sandbox: true), Now);

        Assert.Equal(ClassificationCompletion.Llm, decision.Completion);
        Assert.Single(setup.Client.Requests);
    }

    [Fact]
    public async Task ASandboxConversation_IsReadByThePlainRules_WhenSandboxIsExcluded()
    {
        var setup = New(_ => Completed(Answer()), includeSandbox: false);

        var decision = await setup.Coordinator.DecideAsync(Refused, Context(sandbox: true), Now);

        Assert.Empty(setup.Client.Requests);
        Assert.Equal((ClassificationCompletion.Rules, false, "sandbox_excluded"), (decision.Completion, decision.Applied!.IsRulesFallback, decision.Reason));
    }

    [Theory]
    [InlineData(AiAdmission.AiDisabled)]
    [InlineData(AiAdmission.TenantExhausted)]
    [InlineData(AiAdmission.DeploymentCapReached)]
    public async Task ANotAdmittedTenant_IsReadByTheRules_WithTheFallbackProtection_UncountedAndCleared(AiAdmission admission)
    {
        var setup = New(_ => Completed(Answer()), admission: admission);

        var decision = await setup.Coordinator.DecideAsync(Refused, Context(), Now);

        Assert.Empty(setup.Client.Requests);
        Assert.Equal((ClassificationCompletion.Rules, true, false), (decision.Completion, decision.Applied!.IsRulesFallback, decision.CountAsLlmFailure));
    }

    [Fact]
    public async Task WithTheRealClientRestrictedToSandbox_ARealConversationIsReadByThePlainRules_WithoutACall()
    {
        var setup = New(_ => Completed(Answer()), sandboxOnly: true);

        var real = await setup.Coordinator.DecideAsync(Refused, Context(sandbox: false), Now);
        var sandbox = await setup.Coordinator.DecideAsync(Refused, Context(sandbox: true), Now);

        Assert.Equal((ClassificationCompletion.Rules, false, false, "real_client_sandbox_only"),
            (real.Completion, real.Applied!.IsRulesFallback, real.CountAsLlmFailure, real.Reason));
        Assert.Equal(ClassificationCompletion.Llm, sandbox.Completion);
        Assert.Single(setup.Client.Requests);
    }

    [Fact]
    public async Task AnUnusableAnswer_NeitherResetsNorFeedsTheBreaker_SoAlternatingTimeoutsStillOpenIt()
    {
        var calls = 0;
        var setup = New(_ => ++calls % 2 == 1 ? throw new LlmCallException(LlmFailureKind.Timeout, "FakeException") : Completed("{}"), breakerFailures: 2);

        await setup.Coordinator.DecideAsync(Refused, Context(), Now); // timeout (1)
        await setup.Coordinator.DecideAsync(Refused, Context(), Now); // invalid output: no reset
        await setup.Coordinator.DecideAsync(Refused, Context(), Now); // timeout (2): opens

        Assert.Equal(BreakerState.Open, setup.Breaker.State(Now));
    }

    [Fact]
    public async Task AnUnusableAnswerToTheProbe_FreesTheProbeSlot_WithoutClosingTheBreaker()
    {
        var calls = 0;
        var setup = New(_ => ++calls == 1 ? throw new LlmCallException(LlmFailureKind.Timeout, "FakeException") : Completed(null, LlmStopReason.Refusal), breakerFailures: 1);
        await setup.Coordinator.DecideAsync(Refused, Context(), Now);
        var due = Now.AddSeconds(300);

        var probe = await setup.Coordinator.DecideAsync(Refused, Context(), due);

        Assert.Equal(LlmFailureKind.Refused, probe.FailureKind);
        Assert.Equal(BreakerState.HalfOpen, setup.Breaker.State(due));
        Assert.True(setup.Breaker.TryEnter(due).IsProbe);
    }

    [Fact]
    public async Task APermanentMisconfiguration_OpensTheBreakerOnTheFirstCall_AndNothingElseIsCalledDuringTheCooldown()
    {
        // A 401/403/404 (bad key, no permission, unknown model) repeats on every call: one call, then the rules until the cooldown ends.
        var setup = New(Throws(LlmFailureKind.Misconfigured), breakerFailures: 5);

        var first = await setup.Coordinator.DecideAsync(Refused, Context(), Now);
        var later = new List<ClassificationDecision>();
        for (var i = 0; i < 5; i++)
        {
            later.Add(await setup.Coordinator.DecideAsync(Refused, Context(), Now.AddSeconds(i + 1)));
        }

        Assert.Equal((ClassificationCompletion.RulesFallback, (LlmFailureKind?)LlmFailureKind.Misconfigured, false, "misconfigured"),
            (first.Completion, first.FailureKind, first.CountAsLlmFailure, first.Reason));
        Assert.Equal("misconfigured", first.Call!.Status);
        Assert.Single(setup.Client.Requests);
        Assert.All(later, d => Assert.Equal("breaker_open", d.Reason));
    }

    [Fact]
    public async Task ATransportFailure_IsRecordedAtTheTimeTheCallEnded()
    {
        var clock = new FixedTimeProvider(Now);
        var setup = New(_ =>
        {
            clock.Advance(TimeSpan.FromSeconds(20)); // the call took 20 s
            throw new LlmCallException(LlmFailureKind.Timeout, "FakeException");
        }, breakerFailures: 1, clock: clock);

        await setup.Coordinator.DecideAsync(Refused, Context(), Now);

        var ticket = setup.Breaker.TryEnter(Now.AddSeconds(310));
        Assert.Equal((false, (DateTime?)Now.AddSeconds(320)), (ticket.Admitted, ticket.ReopensAt));
    }

    [Fact]
    public async Task TheNullMeter_AdmitsEveryTenant()
        => Assert.Equal(AiAdmission.Allowed, await NullAiUsageMeter.Instance.CheckAdmissionAsync(Guid.NewGuid()));

    [Fact]
    public void ThePrice_IsPerMillionTokens_ForEachKindOfToken()
    {
        var price = new LlmPricing(InputPerMTok: 0.10m, OutputPerMTok: 0.50m, CacheReadPerMTok: 0.01m, CacheWritePerMTok: 0.125m);

        Assert.Equal(0.10m + 0.50m + 0.01m + 0.125m, price.Estimate(new LlmUsage(1_000_000, 1_000_000, 1_000_000, 1_000_000)));
        Assert.Equal(0.000150m, price.Estimate(new LlmUsage(1000, 100, 0, 0)));
        Assert.Equal(price, LlmPricing.Default);
    }
}
