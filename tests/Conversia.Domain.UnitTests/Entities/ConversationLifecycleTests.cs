using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>Closing and reopening a conversation, the advisor, and what a person or the system may decide about an opportunity.</summary>
public class ConversationLifecycleTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static Conversation NewConversation()
        => Conversation.Start(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0, ConversationSource.Organic);

    private static Opportunity NewOpportunity() => Opportunity.Open(Tenant, Guid.NewGuid());

    // ---- Conversation: close and reopen -------------------------------------------------------

    [Fact]
    public void ANewConversation_IsOpenWithoutAClosingTime()
    {
        var conversation = NewConversation();

        Assert.Equal((ConversationStatus.Open, (DateTime?)null), (conversation.Status, conversation.ClosedAt));
    }

    [Fact]
    public void Close_ClosesTheConversationAtTheGivenTime()
    {
        var conversation = NewConversation();

        conversation.Close(T0.AddDays(8));

        Assert.Equal((ConversationStatus.Closed, (DateTime?)T0.AddDays(8)), (conversation.Status, conversation.ClosedAt));
    }

    [Fact]
    public void Close_AtTheTimeOfTheLastMessage_IsAllowed()
    {
        var conversation = NewConversation();

        conversation.Close(T0);

        Assert.Equal(ConversationStatus.Closed, conversation.Status);
    }

    [Fact]
    public void Close_RefusesAClosedConversation_AndKeepsTheOriginalClosingTime()
    {
        var conversation = NewConversation();
        conversation.Close(T0.AddDays(8));

        Assert.Throws<DomainException>(() => conversation.Close(T0.AddDays(9)));

        Assert.Equal(T0.AddDays(8), conversation.ClosedAt);
    }

    [Fact]
    public void Close_RefusesATimeBeforeTheLastMessage_AndChangesNothing()
    {
        var conversation = NewConversation();

        Assert.Throws<DomainException>(() => conversation.Close(T0.AddSeconds(-1)));

        Assert.Equal((ConversationStatus.Open, (DateTime?)null), (conversation.Status, conversation.ClosedAt));
    }

    [Fact]
    public void Close_RequiresAUtcTime()
        => Assert.Throws<DomainException>(() => NewConversation().Close(new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Unspecified)));

    // ---- The audit trail: who assigned the advisor, who decided the stage ---------------------------------------------------

    [Fact]
    public void AssigningAnAdvisor_RecordsWhoDidItAndWhen()
    {
        var conversation = NewConversation();
        var advisor = Guid.NewGuid();
        var user = Guid.NewGuid();

        conversation.AssignAdvisor(advisor, user, T0.AddHours(1));

        Assert.Equal((advisor, user, (DateTime?)T0.AddHours(1)), (conversation.AdvisorId, conversation.AdvisorAssignedBy, conversation.AdvisorAssignedAt));
    }

    [Fact]
    public void AssigningWithoutAnActor_LeavesNobodyOnRecord()
    {
        var conversation = NewConversation();
        conversation.AssignAdvisor(Guid.NewGuid(), Guid.NewGuid(), T0);

        conversation.AssignAdvisor(Guid.NewGuid()); // the system (or a seed) changes it

        Assert.Equal(((Guid?)null, (DateTime?)null), (conversation.AdvisorAssignedBy, conversation.AdvisorAssignedAt));
    }

    [Fact]
    public void Unassigning_RecordsWhoDidItToo()
    {
        var conversation = NewConversation();
        conversation.AssignAdvisor(Guid.NewGuid(), Guid.NewGuid(), T0);
        var user = Guid.NewGuid();

        conversation.UnassignAdvisor(user, T0.AddDays(1));

        Assert.Equal(((Guid?)null, (Guid?)user, (DateTime?)T0.AddDays(1)), (conversation.AdvisorId, conversation.AdvisorAssignedBy, conversation.AdvisorAssignedAt));
    }

    [Fact]
    public void ANewConversation_HasNoAssignmentOnRecord()
    {
        var conversation = NewConversation();

        Assert.Equal(((Guid?)null, (DateTime?)null), (conversation.AdvisorAssignedBy, conversation.AdvisorAssignedAt));
    }

    [Fact]
    public void ADecisionByHand_RecordsTheUser_AndTheSystemMovingItLaterRecordsNobody()
    {
        var opportunity = NewOpportunity();
        var user = Guid.NewGuid();

        opportunity.SetManualOutcome(OpportunityStage.InFollowUp, null, null, T0, user);
        Assert.Equal(user, opportunity.StageChangedBy);

        // The classifier cannot change a manual stage, so the record stays with the person.
        opportunity.ApplyClassification(new ClassificationUpdate(null, null, ObjectionType.None, OpportunityStage.Won, null, 0.9m, null), T0.AddHours(1));
        Assert.Equal((OpportunityStage.InFollowUp, (Guid?)user), (opportunity.Stage, opportunity.StageChangedBy));
    }

    [Fact]
    public void WhenTheSystemChangesTheStage_NobodyIsOnRecord()
    {
        var opportunity = NewOpportunity();

        opportunity.ApplyClassification(new ClassificationUpdate(null, null, ObjectionType.None, OpportunityStage.InFollowUp, null, 0.6m, null), T0);
        Assert.Null(opportunity.StageChangedBy);

        Assert.True(opportunity.MarkLostForNoResponse(T0.AddDays(8)));
        Assert.Equal((OpportunityStage.Lost, (Guid?)null), (opportunity.Stage, opportunity.StageChangedBy));
    }

    [Fact]
    public void ADecisionWithoutAnActor_ClearsAnOlderOne()
    {
        var opportunity = NewOpportunity();
        opportunity.SetManualOutcome(OpportunityStage.Won, null, null, T0, Guid.NewGuid());

        opportunity.SetManualOutcome(OpportunityStage.InFollowUp, null, null, T0.AddHours(1));

        Assert.Null(opportunity.StageChangedBy);
    }

    [Fact]
    public void Unassign_RemovesTheAdvisor()
    {
        var conversation = NewConversation();
        conversation.AssignAdvisor(Guid.NewGuid());

        conversation.UnassignAdvisor();

        Assert.Null(conversation.AdvisorId);
    }

    // ---- Opportunity: a person decides --------------------------------------------------------

    [Fact]
    public void ANewOpportunity_IsNewAndAutomatic()
    {
        var opportunity = NewOpportunity();

        Assert.Equal((OpportunityStage.New, StageSource.Auto, ObjectionType.None), (opportunity.Stage, opportunity.StageSource, opportunity.Objection));
        Assert.Null(opportunity.LossReason);
        Assert.Null(opportunity.ClosedAt);
        Assert.Null(opportunity.StageChangedAt);
    }

    [Fact]
    public void ManualWon_MarksTheStageManual_AndClosesTheOpportunity()
    {
        var opportunity = NewOpportunity();

        opportunity.SetManualOutcome(OpportunityStage.Won, null, null, T0);

        Assert.Equal((OpportunityStage.Won, StageSource.Manual), (opportunity.Stage, opportunity.StageSource));
        Assert.Equal((T0, T0), (opportunity.StageChangedAt, opportunity.ClosedAt));
        Assert.Null(opportunity.LossReason);
    }

    [Fact]
    public void ManualLost_NeedsALossReason_AndKeepsTheDetail()
    {
        var opportunity = NewOpportunity();

        opportunity.SetManualOutcome(OpportunityStage.Lost, LossReason.Price, "  Dijo que en otra clínica le cobran menos  ", T0);

        Assert.Equal((OpportunityStage.Lost, LossReason.Price, "Dijo que en otra clínica le cobran menos"), (opportunity.Stage, opportunity.LossReason, opportunity.LossDetail));
        Assert.Equal(T0, opportunity.ClosedAt);
    }

    [Fact]
    public void ManualLost_WithoutALossReason_IsRefused_AndChangesNothing()
    {
        var opportunity = NewOpportunity();

        Assert.Throws<DomainException>(() => opportunity.SetManualOutcome(OpportunityStage.Lost, null, "detalle", T0));

        Assert.Equal((OpportunityStage.New, StageSource.Auto, (LossReason?)null, (string?)null), (opportunity.Stage, opportunity.StageSource, opportunity.LossReason, opportunity.LossDetail));
    }

    [Theory]
    [InlineData(OpportunityStage.Won)]
    [InlineData(OpportunityStage.InFollowUp)]
    public void ALossReasonOrDetail_ForAStageThatIsNotLost_IsRefused(OpportunityStage stage)
    {
        var opportunity = NewOpportunity();

        Assert.Throws<DomainException>(() => opportunity.SetManualOutcome(stage, LossReason.Price, null, T0));
        Assert.Throws<DomainException>(() => opportunity.SetManualOutcome(stage, null, "no aplica", T0));

        Assert.Equal(OpportunityStage.New, opportunity.Stage);
    }

    [Fact]
    public void ManualOutcome_RefusesNewAndUndefinedStages()
    {
        var opportunity = NewOpportunity();

        Assert.Throws<DomainException>(() => opportunity.SetManualOutcome(OpportunityStage.New, null, null, T0));
        Assert.Throws<DomainException>(() => opportunity.SetManualOutcome((OpportunityStage)99, null, null, T0));
        Assert.Throws<DomainException>(() => opportunity.SetManualOutcome(OpportunityStage.Lost, (LossReason)99, null, T0));
    }

    [Fact]
    public void ManualOutcome_AcceptsTheDetailMaximumAndRefusesOneMore()
    {
        var opportunity = NewOpportunity();

        opportunity.SetManualOutcome(OpportunityStage.Lost, LossReason.Other, new string('a', Opportunity.DetailMaxLength), T0);
        Assert.Equal(Opportunity.DetailMaxLength, opportunity.LossDetail!.Length);

        Assert.Throws<DomainException>(() =>
            opportunity.SetManualOutcome(OpportunityStage.Lost, LossReason.Other, new string('a', Opportunity.DetailMaxLength + 1), T0));
    }

    // Lone surrogates cannot travel through attribute arguments (the metadata would replace them), so the values are built here.
    [Fact]
    public void ManualOutcome_RefusesNulAndUnpairedSurrogates_AndChangesNothing()
    {
        foreach (var detail in new[] { "hola\0mundo", "\ud83d solo", "\ude00 solo" })
        {
            var opportunity = NewOpportunity();

            Assert.Throws<DomainException>(() => opportunity.SetManualOutcome(OpportunityStage.Lost, LossReason.Other, detail, T0));

            Assert.Equal((OpportunityStage.New, StageSource.Auto), (opportunity.Stage, opportunity.StageSource));
        }
    }

    [Fact]
    public void ManualOutcome_AcceptsAWellFormedEmoji()
    {
        var opportunity = NewOpportunity();

        opportunity.SetManualOutcome(OpportunityStage.Lost, LossReason.Other, "Se fue molesta 😞", T0);

        Assert.Equal("Se fue molesta 😞", opportunity.LossDetail);
    }

    [Fact]
    public void ReturningToInFollowUp_ReopensTheOpportunity_AndClearsTheLoss()
    {
        var opportunity = NewOpportunity();
        opportunity.SetManualOutcome(OpportunityStage.Lost, LossReason.Price, "caro", T0);

        opportunity.SetManualOutcome(OpportunityStage.InFollowUp, null, null, T0.AddHours(1));

        Assert.Equal((OpportunityStage.InFollowUp, StageSource.Manual, (LossReason?)null, (string?)null, (DateTime?)null), (opportunity.Stage, opportunity.StageSource, opportunity.LossReason, opportunity.LossDetail, opportunity.ClosedAt));
        Assert.Equal(T0.AddHours(1), opportunity.StageChangedAt);
    }

    [Fact]
    public void WonToLost_TakesTheNewClosingTime_ButWonToWonKeepsTheFirstOne()
    {
        var opportunity = NewOpportunity();
        opportunity.SetManualOutcome(OpportunityStage.Won, null, null, T0);

        opportunity.SetManualOutcome(OpportunityStage.Won, null, null, T0.AddHours(2));
        Assert.Equal(T0, opportunity.ClosedAt);

        opportunity.SetManualOutcome(OpportunityStage.Lost, LossReason.Timing, null, T0.AddHours(3));
        Assert.Equal(T0.AddHours(3), opportunity.ClosedAt);
    }

    // ---- Opportunity: lost for lack of an answer ---------------------------------------------

    [Fact]
    public void NoResponse_LosesAnOpenAutomaticOpportunity()
    {
        var opportunity = NewOpportunity();

        var changed = opportunity.MarkLostForNoResponse(T0.AddDays(8));

        Assert.True(changed);
        Assert.Equal((OpportunityStage.Lost, LossReason.NoResponse, StageSource.Auto), (opportunity.Stage, opportunity.LossReason, opportunity.StageSource));
        Assert.Equal((T0.AddDays(8), T0.AddDays(8)), (opportunity.ClosedAt, opportunity.StageChangedAt));
    }

    [Fact]
    public void NoResponse_LeavesAManualOpportunityAlone()
    {
        var opportunity = NewOpportunity();
        opportunity.SetManualOutcome(OpportunityStage.InFollowUp, null, null, T0);

        var changed = opportunity.MarkLostForNoResponse(T0.AddDays(8));

        Assert.False(changed);
        Assert.Equal((OpportunityStage.InFollowUp, StageSource.Manual, (LossReason?)null), (opportunity.Stage, opportunity.StageSource, opportunity.LossReason));
    }

    [Theory]
    [InlineData(OpportunityStage.Won)]
    [InlineData(OpportunityStage.Lost)]
    public void NoResponse_LeavesAClosedOpportunityAlone(OpportunityStage closed)
    {
        var opportunity = NewOpportunity();
        opportunity.SetManualOutcome(closed, closed == OpportunityStage.Lost ? LossReason.Price : null, null, T0);

        Assert.False(opportunity.MarkLostForNoResponse(T0.AddDays(8)));

        Assert.Equal((closed, T0), (opportunity.Stage, opportunity.ClosedAt));
    }

    // ---- TextRules: surrogates -----------------------------------------------------------------

    [Fact]
    public void HasUnpairedSurrogate_FindsOnlyBrokenPairs()
    {
        foreach (var text in new[] { "hola", "😞", "a😞b" })
        {
            Assert.False(TextRules.HasUnpairedSurrogate(text), text);
        }

        foreach (var text in new[] { "\ud83d", "\ud83dx", "x\ude00", "\ude00\ud83d" }) // the last one is a reversed pair
        {
            Assert.True(TextRules.HasUnpairedSurrogate(text), text);
        }
    }

    [Fact]
    public void TheSharedTextRule_RejectsUnpairedSurrogates_ForRequiredAndOptionalText()
    {
        Assert.False(TextRules.IsValidRequired("ok \ud83d", 50));
        Assert.False(TextRules.IsValidOptional("ok \ud83d", 50));
        Assert.True(TextRules.IsValidOptional("ok 😞", 50));
        Assert.True(TextRules.IsValidOptional("   ", 50));
    }
}
