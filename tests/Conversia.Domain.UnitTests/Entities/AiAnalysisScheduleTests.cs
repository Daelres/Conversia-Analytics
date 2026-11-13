using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>
/// When the LLM reads a company's conversations (the per-company schedule) and the conversation's own LLM analysis state, apart from the
/// rules' near-real-time pass (docs/CLASSIFICATION.md, "When the AI reads").
/// </summary>
public class AiAnalysisScheduleTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);
    private static readonly string Hash = new('a', 64);
    private static readonly string OtherHash = new('b', 64);

    // ---- The company's schedule ----

    [Fact]
    public void ANewCompany_IsAnalysedNightly_AtTheDeploymentDefaultHour()
    {
        var tenant = Tenant.Create("Aurora");

        Assert.Equal((AiAnalysisMode.Nightly, (int?)null, (int?)null), (tenant.AiAnalysisMode, tenant.AiAnalysisNightlyHour, tenant.AiAnalysisIntervalHours));
    }

    [Theory]
    [InlineData(AiAnalysisMode.Nightly, 3, null)]
    [InlineData(AiAnalysisMode.Nightly, 0, null)]
    [InlineData(AiAnalysisMode.Nightly, 23, null)]
    [InlineData(AiAnalysisMode.EveryHours, null, 1)]
    [InlineData(AiAnalysisMode.EveryHours, null, 24)]
    [InlineData(AiAnalysisMode.NearRealTime, null, null)]
    public void AValidSchedule_IsStored_AndTheSameAgainChangesNothing(AiAnalysisMode mode, int? hour, int? interval)
    {
        var tenant = Tenant.Create("Aurora");

        Assert.True(tenant.SetAiAnalysisSchedule(mode, hour, interval));
        Assert.Equal((mode, hour, interval), (tenant.AiAnalysisMode, tenant.AiAnalysisNightlyHour, tenant.AiAnalysisIntervalHours));
        Assert.False(tenant.SetAiAnalysisSchedule(mode, hour, interval));
    }

    [Fact]
    public void BackToTheDefault_IsAChange()
    {
        var tenant = Tenant.Create("Aurora");
        tenant.SetAiAnalysisSchedule(AiAnalysisMode.EveryHours, null, 6);

        Assert.True(tenant.SetAiAnalysisSchedule(AiAnalysisMode.Nightly, null, null));
        Assert.Equal((AiAnalysisMode.Nightly, (int?)null, (int?)null), (tenant.AiAnalysisMode, tenant.AiAnalysisNightlyHour, tenant.AiAnalysisIntervalHours));
    }

    [Theory]
    [InlineData(AiAnalysisMode.Nightly, -1, null)]
    [InlineData(AiAnalysisMode.Nightly, 24, null)]
    [InlineData(AiAnalysisMode.Nightly, 2, 6)]
    [InlineData(AiAnalysisMode.EveryHours, null, null)]
    [InlineData(AiAnalysisMode.EveryHours, null, 0)]
    [InlineData(AiAnalysisMode.EveryHours, null, 25)]
    [InlineData(AiAnalysisMode.EveryHours, 2, 6)]
    [InlineData(AiAnalysisMode.NearRealTime, 2, null)]
    [InlineData(AiAnalysisMode.NearRealTime, null, 4)]
    [InlineData((AiAnalysisMode)42, null, null)]
    public void AnInvalidSchedule_IsRefused_AndNothingChanges(AiAnalysisMode mode, int? hour, int? interval)
    {
        var tenant = Tenant.Create("Aurora");
        tenant.SetAiAnalysisSchedule(AiAnalysisMode.Nightly, 2, null);

        var ex = Assert.Throws<DomainException>(() => tenant.SetAiAnalysisSchedule(mode, hour, interval));

        Assert.Equal(Tenant.AiAnalysisScheduleRuleMessage, ex.Message);
        Assert.Equal((AiAnalysisMode.Nightly, (int?)2, (int?)null), (tenant.AiAnalysisMode, tenant.AiAnalysisNightlyHour, tenant.AiAnalysisIntervalHours));
    }

    // ---- The conversation's LLM state ----

    private static Conversation NewConversation() => Conversation.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0, ConversationSource.Organic);

    [Fact]
    public void ANewMessage_MakesTheConversationPendingForTheLlm_WithoutTouchingTheLeaseOrTheAttempt()
    {
        var conversation = NewConversation();
        conversation.LeaseLlmAnalysis(T0.AddMinutes(1));
        conversation.DiscardLlmAnalysis(T0.AddSeconds(30)); // attempted inside a window

        conversation.RequestClassification(T0.AddMinutes(2));

        Assert.True(conversation.LlmPending);
        // The attempt time is what keeps a window from analysing the conversation twice: a message never resets it.
        Assert.Equal(T0.AddSeconds(30), conversation.LlmAttemptedAt);
    }

    [Fact]
    public void TheRulesPass_ClearsOnlyTheRulesFlag_AndKeepsTheLlmStateAndTheHash()
    {
        var conversation = NewConversation();
        conversation.RequestClassification(T0);
        conversation.CompleteLlmAnalysis(T0.AddMinutes(1), Hash);
        conversation.RequestClassification(T0.AddMinutes(2));

        conversation.CompleteRulesPass(T0.AddMinutes(3));

        Assert.Equal((false, 0, (DateTime?)T0.AddMinutes(3)), (conversation.ClassificationDirty, conversation.ClassificationFailures, conversation.ClassificationAttemptedAt));
        Assert.Equal((true, Hash), (conversation.LlmPending, conversation.LastLlmSnapshotHash));
    }

    [Fact]
    public void AnAppliedReading_ClearsThePendingAndTheLease_ResetsTheFailures_AndRecordsTheHashAndTheTimes()
    {
        var conversation = NewConversation();
        conversation.RequestClassification(T0);
        conversation.RecordLlmContentFailure(T0, 5);
        conversation.LeaseLlmAnalysis(T0.AddMinutes(1));

        conversation.CompleteLlmAnalysis(T0.AddSeconds(20), Hash);

        Assert.Equal((false, (DateTime?)null, 0, Hash), (conversation.LlmPending, conversation.LlmLeaseUntil, conversation.LlmFailures, conversation.LastLlmSnapshotHash));
        Assert.Equal(((DateTime?)T0.AddSeconds(20), (DateTime?)T0.AddSeconds(20)), (conversation.LlmAttemptedAt, conversation.LlmAnalyzedAt));
    }

    [Fact]
    public void AnAppliedReading_RefusesABadHash()
    {
        var conversation = NewConversation();

        Assert.Throws<DomainException>(() => conversation.CompleteLlmAnalysis(T0, "not-a-hash"));
    }

    [Fact]
    public void AnUnchangedConversation_IsNoLongerPending_AndKeepsItsHashAndItsLastAnalysisTime()
    {
        var conversation = NewConversation();
        conversation.CompleteLlmAnalysis(T0, Hash);
        conversation.RequestClassification(T0.AddHours(1));
        conversation.LeaseLlmAnalysis(T0.AddHours(2));

        conversation.ConfirmLlmAnalysisUnchanged(T0.AddHours(1).AddMinutes(1));

        Assert.Equal((false, (DateTime?)null, Hash), (conversation.LlmPending, conversation.LlmLeaseUntil, conversation.LastLlmSnapshotHash));
        Assert.Equal(((DateTime?)T0.AddHours(1).AddMinutes(1), (DateTime?)T0), (conversation.LlmAttemptedAt, conversation.LlmAnalyzedAt));
    }

    [Fact]
    public void ASkippedConversation_IsNoLongerPending_AndForgetsTheHash()
    {
        var conversation = NewConversation();
        conversation.CompleteLlmAnalysis(T0, Hash);
        conversation.RequestClassification(T0.AddHours(1));
        conversation.LeaseLlmAnalysis(T0.AddHours(2));

        conversation.SkipLlmAnalysis(T0.AddHours(1).AddMinutes(1));

        Assert.Equal((false, (DateTime?)null, (string?)null), (conversation.LlmPending, conversation.LlmLeaseUntil, conversation.LastLlmSnapshotHash));
        Assert.Equal((DateTime?)T0.AddHours(1).AddMinutes(1), conversation.LlmAttemptedAt);
    }

    [Fact]
    public void ContentFailures_KeepTheConversationPending_UpToTheRetries_ThenTheRulesStand()
    {
        var conversation = NewConversation();
        conversation.RequestClassification(T0);
        conversation.LeaseLlmAnalysis(T0.AddMinutes(5));

        Assert.True(conversation.RecordLlmContentFailure(T0.AddMinutes(1), maxFallbackRetries: 1));
        Assert.Equal((1, true, (DateTime?)null, (DateTime?)T0.AddMinutes(1)), (conversation.LlmFailures, conversation.LlmPending, conversation.LlmLeaseUntil, conversation.LlmAttemptedAt));

        Assert.False(conversation.RecordLlmContentFailure(T0.AddMinutes(2), maxFallbackRetries: 1));
        Assert.Equal((2, false), (conversation.LlmFailures, conversation.LlmPending));
        Assert.Throws<DomainException>(() => conversation.RecordLlmContentFailure(T0, -1));
    }

    [Fact]
    public void ADeferral_KeepsThePending_AndMovesTheLease_WithoutCountingAnAttempt()
    {
        var conversation = NewConversation();
        conversation.RequestClassification(T0);
        conversation.LeaseLlmAnalysis(T0.AddMinutes(1));

        conversation.DeferLlmAnalysis(T0.AddMinutes(10));

        Assert.Equal((true, (DateTime?)T0.AddMinutes(10), (DateTime?)null, 0), (conversation.LlmPending, conversation.LlmLeaseUntil, conversation.LlmAttemptedAt, conversation.LlmFailures));
    }

    [Fact]
    public void ADiscard_ReleasesTheLease_KeepsThePending_AndRecordsTheAttempt()
    {
        var conversation = NewConversation();
        conversation.RequestClassification(T0);
        conversation.CompleteLlmAnalysis(T0, Hash);
        conversation.RequestClassification(T0.AddMinutes(1));
        conversation.LeaseLlmAnalysis(T0.AddMinutes(3));

        conversation.DiscardLlmAnalysis(T0.AddMinutes(2));

        Assert.Equal((true, (DateTime?)null, (DateTime?)T0.AddMinutes(2), Hash, (DateTime?)T0), (conversation.LlmPending, conversation.LlmLeaseUntil, conversation.LlmAttemptedAt, conversation.LastLlmSnapshotHash, conversation.LlmAnalyzedAt));
        Assert.NotEqual(OtherHash, conversation.LastLlmSnapshotHash);
    }

    // ---- Review round: ownership of the rules-only path and of a batch ----

    [Fact]
    public void TheRulesOnlyReading_LeavesNothingPendingForTheLlm()
    {
        var conversation = NewConversation();
        conversation.RequestClassification(T0);

        conversation.CompleteClassification(T0.AddMinutes(1));

        Assert.False(conversation.LlmPending);
    }

    [Fact]
    public void ABatchIsMarkedSubmittedOnlyFromPreparing_AFailedOneStaysFailed()
    {
        var batch = LlmBatch.Prepare(2, T0);
        batch.MarkSubmitStarted(2, T0.AddSeconds(1));
        Assert.Equal((2, (DateTime?)T0.AddSeconds(1)), (batch.RequestCount, batch.SubmitStartedAt));

        Assert.True(batch.MarkSubmitted("msgbatch_1", null, T0.AddSeconds(2)));
        Assert.Equal(LlmBatchStatus.Submitted, batch.Status);

        var failed = LlmBatch.Prepare(1, T0);
        failed.MarkFailed("abandoned", T0);
        Assert.False(failed.MarkSubmitted("msgbatch_2", null, T0));
        Assert.Equal((LlmBatchStatus.Failed, (string?)null), (failed.Status, failed.ProviderBatchId));
    }

    [Fact]
    public void ABatchRequest_KeepsItsLeaseAndItsEstimatedCost()
    {
        var request = LlmBatchRequest.For(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0, Hash, false, T0, OpportunityStage.New, null, T0,
            leaseUntil: T0.AddHours(25), estimatedUsd: 0.0002m);

        Assert.Equal(((DateTime?)T0.AddHours(25), 0.0002m), (request.LeaseUntil, request.EstimatedUsd));
    }
}
