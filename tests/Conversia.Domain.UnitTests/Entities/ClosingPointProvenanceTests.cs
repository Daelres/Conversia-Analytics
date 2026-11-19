using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>
/// The closing point (how far the conversation got, whether it moved off the chat, who closed it) follows the provenance rules of the other
/// extracted fields: per-field source and producer, a person always wins, an explicit rules fallback never erases an LLM value.
/// </summary>
public class ClosingPointProvenanceTests
{
    private static readonly DateTime T0 = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid User = Guid.NewGuid();

    private static Opportunity NewOpportunity() => Opportunity.Open(Guid.NewGuid(), Guid.NewGuid());

    private static readonly ClosingPointReading Llm = new(ReachedStage.PriceShared, OffChannelMove.PhoneCall, 7, ClosedBy.Advisor, true);
    private static readonly ClosingPointReading Rules = new(ReachedStage.FirstContact, OffChannelMove.None, null, ClosedBy.Unknown, false);

    private static ClassificationUpdate Reading(ClosingPointReading? point, ClassifierProvider provider = ClassifierProvider.Rules, bool fallback = false)
        => new(null, null, ObjectionType.None, OpportunityStage.InFollowUp, null, 0.6m, null, provider, fallback, point);

    private static (ReachedStage?, OffChannelMove, int?, ClosedBy, bool) Values(Opportunity o) => (o.ReachedStage, o.MovedOffChannel, o.OffChannelEvidenceIndex, o.ClosedBy, o.ClosedOutsideSystem);

    private static (ClassifierProvider?, ClassifierProvider?, ClassifierProvider?, ClassifierProvider?) Producers(Opportunity o) => (o.ReachedStageClassifiedBy, o.MovedOffChannelClassifiedBy, o.ClosedByClassifiedBy, o.ClosedOutsideSystemClassifiedBy);

    [Fact]
    public void ANewOpportunity_HasNoClosingPoint_AndAutoSources()
    {
        var o = NewOpportunity();

        Assert.Equal(((ReachedStage?)null, OffChannelMove.None, (int?)null, ClosedBy.Unknown, false), Values(o));
        Assert.Equal((FieldSource.Auto, FieldSource.Auto, FieldSource.Auto, FieldSource.Auto),
            (o.ReachedStageSource, o.MovedOffChannelSource, o.ClosedBySource, o.ClosedOutsideSystemSource));
    }

    [Fact]
    public void AReading_WritesEveryField_AndItsProducer()
    {
        var o = NewOpportunity();

        o.ApplyClassification(Reading(Llm, ClassifierProvider.Llm), T0);

        Assert.Equal((ReachedStage.PriceShared, OffChannelMove.PhoneCall, 7, ClosedBy.Advisor, true), Values(o));
        Assert.Equal((ClassifierProvider.Llm, ClassifierProvider.Llm, ClassifierProvider.Llm, ClassifierProvider.Llm), Producers(o));
    }

    [Fact]
    public void AReadingWithoutAClosingPoint_LeavesItAlone()
    {
        var o = NewOpportunity();
        o.ApplyClassification(Reading(Llm, ClassifierProvider.Llm), T0);

        o.ApplyClassification(Reading(null), T0.AddMinutes(1));

        Assert.Equal((ReachedStage.PriceShared, OffChannelMove.PhoneCall, 7, ClosedBy.Advisor, true), Values(o));
    }

    [Fact]
    public void AnExplicitRulesFallback_NeverErasesNonEmptyLlmValues()
    {
        var o = NewOpportunity();
        o.ApplyClassification(Reading(Llm, ClassifierProvider.Llm), T0);

        o.ApplyClassification(Reading(Rules, fallback: true), T0.AddMinutes(1));

        Assert.Equal((ReachedStage.PriceShared, OffChannelMove.PhoneCall, 7, ClosedBy.Advisor, true), Values(o));
        Assert.Equal((ClassifierProvider.Llm, ClassifierProvider.Llm, ClassifierProvider.Llm, ClassifierProvider.Llm), Producers(o));
    }

    [Fact]
    public void AnExplicitRulesFallback_FillsWhatTheLlmLeftEmpty()
    {
        var o = NewOpportunity();
        o.ApplyClassification(Reading(ClosingPointReading.Empty, ClassifierProvider.Llm), T0);

        o.ApplyClassification(Reading(new ClosingPointReading(ReachedStage.ObjectionRaised, OffChannelMove.InPerson, 3, ClosedBy.Unknown, true), fallback: true), T0.AddMinutes(1));

        Assert.Equal((ReachedStage.ObjectionRaised, OffChannelMove.InPerson, 3, ClosedBy.Unknown, true), Values(o));
        Assert.Equal(ClassifierProvider.Rules, o.ReachedStageClassifiedBy);
    }

    [Fact]
    public void APlainRulesReading_OverwritesLlmValues_AsTheOtherFieldsDo()
    {
        var o = NewOpportunity();
        o.ApplyClassification(Reading(Llm, ClassifierProvider.Llm), T0);

        o.ApplyClassification(Reading(Rules) with { Stage = OpportunityStage.Lost, LossReason = LossReason.Price }, T0.AddMinutes(1)); // the outcome changed

        Assert.Equal((ReachedStage.FirstContact, OffChannelMove.None, (int?)null, ClosedBy.Unknown, false), Values(o));
        Assert.Equal((ClassifierProvider.Rules, ClassifierProvider.Rules, ClassifierProvider.Rules, ClassifierProvider.Rules), Producers(o));
    }

    [Fact]
    public void TheReachedStage_NeverGoesDown_WhileTheOutcomeStaysTheSame()
    {
        // A long chat read from its latest 200 messages, or a weaker reader, must not forget how far the conversation already got.
        var o = NewOpportunity();
        o.ApplyClassification(Reading(Llm, ClassifierProvider.Llm), T0);

        o.ApplyClassification(Reading(Rules), T0.AddMinutes(1));

        Assert.Equal((ReachedStage?)ReachedStage.PriceShared, o.ReachedStage);
        Assert.Equal(ClassifierProvider.Llm, o.ReachedStageClassifiedBy);
        Assert.Equal(OffChannelMove.None, o.MovedOffChannel); // only the stage is monotonic
    }

    [Fact]
    public void TheReachedStage_GoesUp_AndFollowsAChangedOutcome()
    {
        var o = NewOpportunity();
        o.ApplyClassification(Reading(Rules), T0);

        o.ApplyClassification(Reading(Llm, ClassifierProvider.Llm), T0.AddMinutes(1));
        Assert.Equal((ReachedStage?)ReachedStage.PriceShared, o.ReachedStage);

        o.ApplyClassification(Reading(Rules) with { Stage = OpportunityStage.Lost, LossReason = LossReason.Price }, T0.AddMinutes(2));
        Assert.Equal((ReachedStage?)ReachedStage.FirstContact, o.ReachedStage);
    }

    [Fact]
    public void ManualFields_AreNeverOverwritten_AndRevertingHandsThemBack()
    {
        var o = NewOpportunity();
        o.SetManualReachedStage(ReachedStage.AppointmentProposed, T0, User);
        o.SetManualMovedOffChannel(OffChannelMove.OtherChannel, T0, User);
        o.SetManualClosedBy(ClosedBy.CustomerSelfService, T0, User);
        o.SetManualClosedOutsideSystem(true, T0, User);

        o.ApplyClassification(Reading(Rules), T0.AddMinutes(1));

        Assert.Equal((ReachedStage.AppointmentProposed, OffChannelMove.OtherChannel, (int?)null, ClosedBy.CustomerSelfService, true), Values(o));
        Assert.Equal((FieldSource.Manual, FieldSource.Manual, FieldSource.Manual, FieldSource.Manual),
            (o.ReachedStageSource, o.MovedOffChannelSource, o.ClosedBySource, o.ClosedOutsideSystemSource));
        Assert.Equal((T0, (Guid?)User), (o.DetailsChangedAt!.Value, o.DetailsChangedBy));

        foreach (var field in new[] { OpportunityDetailField.ReachedStage, OpportunityDetailField.MovedOffChannel, OpportunityDetailField.ClosedBy, OpportunityDetailField.ClosedOutsideSystem })
        {
            o.RevertDetailToAuto(field, T0.AddMinutes(2), User);
        }

        o.ApplyClassification(Reading(Llm, ClassifierProvider.Llm), T0.AddMinutes(3));
        Assert.Equal((ReachedStage.PriceShared, OffChannelMove.PhoneCall, 7, ClosedBy.Advisor, true), Values(o));
    }

    [Fact]
    public void AManualMove_ClearsTheEvidenceLine()
    {
        var o = NewOpportunity();
        o.ApplyClassification(Reading(Llm, ClassifierProvider.Llm), T0);

        o.SetManualMovedOffChannel(OffChannelMove.PhoneCall, T0, User);

        Assert.Null(o.OffChannelEvidenceIndex);
    }

    [Fact]
    public void TheReservedAiCloser_AndUndefinedValues_AreRefused()
    {
        var o = NewOpportunity();

        Assert.Throws<DomainException>(() => o.SetManualClosedBy(ClosedBy.AiAssistant, T0));
        Assert.Throws<DomainException>(() => o.SetManualClosedBy((ClosedBy)99, T0));
        Assert.Throws<DomainException>(() => o.SetManualReachedStage((ReachedStage)0, T0));
        Assert.Throws<DomainException>(() => o.SetManualMovedOffChannel((OffChannelMove)9, T0));
        Assert.Throws<DomainException>(() => o.ApplyClassification(Reading(Llm with { ClosedBy = ClosedBy.AiAssistant }), T0));
        Assert.Throws<DomainException>(() => o.ApplyClassification(Reading(Llm with { ReachedStage = (ReachedStage)42 }), T0));
        Assert.Throws<DomainException>(() => o.ApplyClassification(Reading(Llm with { OffChannelEvidenceIndex = 0 }), T0));
        Assert.Throws<DomainException>(() => o.ApplyClassification(Reading(Rules with { OffChannelEvidenceIndex = 4 }), T0));
        Assert.Null(o.ClassifiedAt);
    }

    // The AI sales assistant (docs/ASSISTANT.md, "Metrics"): a classifier may say the assistant closed a deal it won in the chat, from the stored
    // authors of the messages; never on an open or lost one, and a person still cannot type it.
    [Fact]
    public void TheAiCloser_IsAcceptedOnAWonReading()
    {
        var o = NewOpportunity();
        var won = new ClassificationUpdate(null, null, ObjectionType.None, OpportunityStage.Won, null, 0.9m, null, ClassifierProvider.Rules, false,
            new ClosingPointReading(ReachedStage.AppointmentBooked, OffChannelMove.None, null, ClosedBy.AiAssistant, false));

        o.ApplyClassification(won, T0);

        Assert.Equal(ClosedBy.AiAssistant, o.ClosedBy);
    }
}
