using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>What a classification may and may not do to an opportunity, and the request/complete cycle of a conversation.</summary>
public class OpportunityClassificationTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static Opportunity NewOpportunity() => Opportunity.Open(Tenant, Guid.NewGuid());

    private static ClassificationUpdate Reading(
        OpportunityStage? stage = OpportunityStage.InFollowUp, LossReason? loss = null, ObjectionType objection = ObjectionType.None,
        string? service = "axilas", string? reason = "precio_promocion", decimal confidence = 0.7m, string? rules = "service.axilas,outcome.in_follow_up")
        => new(service, reason, objection, stage, loss, confidence, rules);

    // ---- The descriptive fields -----------------------------------------------------------------------------

    [Fact]
    public void AClassification_FillsTheDescriptiveFields()
    {
        var opportunity = NewOpportunity();

        opportunity.ApplyClassification(Reading(objection: ObjectionType.Price), T0);

        Assert.Equal(("axilas", "precio_promocion", ObjectionType.Price), (opportunity.ServiceInterest, opportunity.InterestReason, opportunity.Objection));
        Assert.Equal((0.7m, "service.axilas,outcome.in_follow_up", T0), (opportunity.ClassificationConfidence, opportunity.ClassificationRules, opportunity.ClassifiedAt));
    }

    [Fact]
    public void ABlankService_MeansNone_AndTheCodesAreCheckedForShapeAndLength()
    {
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(service: "  ", reason: null), T0);
        Assert.Null(opportunity.ServiceInterest);

        Assert.Throws<DomainException>(() => opportunity.ApplyClassification(Reading(service: "Axilas"), T0)); // upper case
        Assert.Throws<DomainException>(() => opportunity.ApplyClassification(Reading(service: "axilas y piernas"), T0)); // spaces: text, not a code
        Assert.Throws<DomainException>(() => opportunity.ApplyClassification(Reading(service: new string('a', Opportunity.ServiceInterestMaxLength + 1)), T0));
        Assert.Throws<DomainException>(() => opportunity.ApplyClassification(Reading(reason: new string('a', Opportunity.InterestReasonMaxLength + 1)), T0));
        opportunity.ApplyClassification(Reading(service: new string('a', Opportunity.ServiceInterestMaxLength)), T0);
        Assert.Equal(Opportunity.ServiceInterestMaxLength, opportunity.ServiceInterest!.Length);
    }

    [Fact]
    public void ARejectedClassification_ChangesNothing()
    {
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(stage: OpportunityStage.Won), T0);

        Assert.Throws<DomainException>(() => opportunity.ApplyClassification(Reading(stage: OpportunityStage.New, service: "otra", confidence: 0.1m), T0.AddHours(1)));
        Assert.Throws<DomainException>(() => opportunity.ApplyClassification(Reading(confidence: 1.5m, service: "otra"), T0.AddHours(1)));
        Assert.Throws<DomainException>(() => opportunity.ApplyClassification(Reading(stage: OpportunityStage.Won, loss: LossReason.Price, service: "otra"), T0.AddHours(1)));

        Assert.Equal(("axilas", OpportunityStage.Won, T0), (opportunity.ServiceInterest, opportunity.Stage, opportunity.ClassifiedAt));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void TheConfidence_MustBeBetween0And1(double confidence)
        => Assert.Throws<DomainException>(() => NewOpportunity().ApplyClassification(Reading(confidence: (decimal)confidence), T0));

    // ---- The stage ------------------------------------------------------------------------------------------

    [Fact]
    public void AnUndeterminedOutcome_LeavesTheStageAlone()
    {
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(stage: OpportunityStage.InFollowUp), T0);

        opportunity.ApplyClassification(Reading(stage: null, confidence: 0.2m), T0.AddHours(1));

        Assert.Equal((OpportunityStage.InFollowUp, T0), (opportunity.Stage, opportunity.StageChangedAt));
        Assert.Equal(T0.AddHours(1), opportunity.ClassifiedAt);
    }

    [Fact]
    public void WonAndLost_CloseTheOpportunity_WithTheLossReasonOnlyForLost()
    {
        var won = NewOpportunity();
        won.ApplyClassification(Reading(stage: OpportunityStage.Won), T0);
        var lost = NewOpportunity();
        lost.ApplyClassification(Reading(stage: OpportunityStage.Lost, loss: LossReason.Price), T0);

        Assert.Equal((OpportunityStage.Won, StageSource.Auto, T0, (LossReason?)null), (won.Stage, won.StageSource, won.ClosedAt, won.LossReason));
        Assert.Equal((OpportunityStage.Lost, LossReason.Price, T0), (lost.Stage, lost.LossReason, lost.ClosedAt));
    }

    [Fact]
    public void ALostWithoutAReason_IsUnknown()
    {
        var opportunity = NewOpportunity();

        opportunity.ApplyClassification(Reading(stage: OpportunityStage.Lost, loss: null), T0);

        Assert.Equal(LossReason.Unknown, opportunity.LossReason);
    }

    [Fact]
    public void ReadingTheSameThingAgain_ChangesNothingButTheTimeItWasClassified()
    {
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(stage: OpportunityStage.Lost, loss: LossReason.Timing, objection: ObjectionType.Timing), T0);
        var before = (opportunity.Stage, opportunity.StageSource, opportunity.StageChangedAt, opportunity.ClosedAt, opportunity.LossReason, opportunity.Objection, opportunity.ServiceInterest, opportunity.InterestReason, opportunity.ClassificationConfidence, opportunity.ClassificationRules);

        opportunity.ApplyClassification(Reading(stage: OpportunityStage.Lost, loss: LossReason.Timing, objection: ObjectionType.Timing), T0.AddMinutes(5));

        Assert.Equal(before, (opportunity.Stage, opportunity.StageSource, opportunity.StageChangedAt, opportunity.ClosedAt, opportunity.LossReason, opportunity.Objection, opportunity.ServiceInterest, opportunity.InterestReason, opportunity.ClassificationConfidence, opportunity.ClassificationRules));
        Assert.Equal(T0.AddMinutes(5), opportunity.ClassifiedAt);
    }

    [Fact]
    public void TheStageFollowsTheClassifierWhileItIsAutomatic()
    {
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(stage: OpportunityStage.Won), T0);

        opportunity.ApplyClassification(Reading(stage: OpportunityStage.Lost, loss: LossReason.NotInterested), T0.AddDays(1));
        Assert.Equal((OpportunityStage.Lost, T0.AddDays(1), T0.AddDays(1)), (opportunity.Stage, opportunity.StageChangedAt, opportunity.ClosedAt));

        opportunity.ApplyClassification(Reading(stage: OpportunityStage.InFollowUp), T0.AddDays(2));
        Assert.Equal((OpportunityStage.InFollowUp, (LossReason?)null, (DateTime?)null), (opportunity.Stage, opportunity.LossReason, opportunity.ClosedAt));
    }

    // ---- A person always wins -------------------------------------------------------------------------------

    [Theory]
    [InlineData(OpportunityStage.Won)]
    [InlineData(OpportunityStage.InFollowUp)]
    [InlineData(OpportunityStage.Lost)]
    public void AManualStage_IsNeverOverwrittenByTheClassifier_NorItsLossReason(OpportunityStage manual)
    {
        var opportunity = NewOpportunity();
        opportunity.SetManualOutcome(manual, manual == OpportunityStage.Lost ? LossReason.Price : null, manual == OpportunityStage.Lost ? "caro" : null, T0);
        var before = (opportunity.Stage, opportunity.LossReason, opportunity.LossDetail, opportunity.ClosedAt, opportunity.StageChangedAt);

        foreach (var reading in new[]
                 {
                     Reading(stage: OpportunityStage.Won),
                     Reading(stage: OpportunityStage.Lost, loss: LossReason.Timing),
                     Reading(stage: OpportunityStage.InFollowUp),
                     Reading(stage: null),
                 })
        {
            opportunity.ApplyClassification(reading, T0.AddDays(1));
        }

        Assert.Equal(before, (opportunity.Stage, opportunity.LossReason, opportunity.LossDetail, opportunity.ClosedAt, opportunity.StageChangedAt));
        Assert.Equal(StageSource.Manual, opportunity.StageSource);
    }

    [Fact]
    public void WhileTheStageIsManual_TheDescriptiveFieldsStillFollowTheClassifier()
    {
        var opportunity = NewOpportunity();
        opportunity.SetManualOutcome(OpportunityStage.Won, null, null, T0);

        opportunity.ApplyClassification(Reading(service: "piernas", objection: ObjectionType.Pain), T0.AddDays(1));

        Assert.Equal(("piernas", ObjectionType.Pain, OpportunityStage.Won), (opportunity.ServiceInterest, opportunity.Objection, opportunity.Stage));
    }

    // ---- Lost for lack of an answer -------------------------------------------------------------------------

    [Fact]
    public void ALossForNoResponse_IsOnlyReplacedByADecisiveOutcome_NeverByFollowUpOrUndetermined()
    {
        var opportunity = NewOpportunity();
        opportunity.MarkLostForNoResponse(T0);

        opportunity.ApplyClassification(Reading(stage: OpportunityStage.InFollowUp), T0.AddDays(1));
        opportunity.ApplyClassification(Reading(stage: null), T0.AddDays(1));
        Assert.Equal((OpportunityStage.Lost, LossReason.NoResponse, T0), (opportunity.Stage, opportunity.LossReason, opportunity.ClosedAt));

        opportunity.ApplyClassification(Reading(stage: OpportunityStage.Won), T0.AddDays(2));
        Assert.Equal(OpportunityStage.Won, opportunity.Stage);
    }

    [Fact]
    public void ALossForNoResponse_BecomesARealReasonWhenTheClassifierFindsOne()
    {
        var opportunity = NewOpportunity();
        opportunity.MarkLostForNoResponse(T0);

        opportunity.ApplyClassification(Reading(stage: OpportunityStage.Lost, loss: LossReason.Price), T0.AddDays(1));

        Assert.Equal(LossReason.Price, opportunity.LossReason);
    }

    // ---- Conversation: the classification request ------------------------------------------------------------------

    private static Conversation NewConversation()
        => Conversation.Start(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0, ConversationSource.Organic);

    [Fact]
    public void ANewConversation_WaitsForNothing()
    {
        var conversation = NewConversation();

        Assert.Equal((false, 0, (DateTime?)null, (DateTime?)null), (conversation.ClassificationDirty, conversation.ClassificationFailures, conversation.ClassificationAttemptedAt, conversation.ClassificationRequestedAt));
    }

    [Fact]
    public void RequestClassification_MarksItDirty_AndGivesItAFreshStart()
    {
        var conversation = NewConversation();
        conversation.RequestClassification(T0);

        conversation.RequestClassification(T0.AddMinutes(1));

        Assert.Equal((true, 0, (DateTime?)null, (DateTime?)T0.AddMinutes(1)), (conversation.ClassificationDirty, conversation.ClassificationFailures, conversation.ClassificationAttemptedAt, conversation.ClassificationRequestedAt));
    }

    [Fact]
    public void CompleteClassification_ClearsTheFlag_AndTheFailures()
    {
        var conversation = NewConversation();
        conversation.RequestClassification(T0);

        conversation.CompleteClassification(T0.AddMinutes(2));

        Assert.Equal((false, 0, (DateTime?)T0.AddMinutes(2)), (conversation.ClassificationDirty, conversation.ClassificationFailures, conversation.ClassificationAttemptedAt));
    }
}
