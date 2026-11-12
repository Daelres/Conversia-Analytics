using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>
/// Per-field provenance of what a classifier extracts (docs/CLASSIFICATION.md, "A person always wins"): a field a person set is never
/// overwritten, a field the LLM produced survives an explicit rules fallback, and a plain rules run behaves as it always did.
/// </summary>
public class ExtractionProvenanceTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private static Opportunity NewOpportunity() => Opportunity.Open(Tenant, Guid.NewGuid());

    private static ClassificationUpdate Reading(
        string? service = "axilas", string? reason = "estetica", ObjectionType objection = ObjectionType.Price,
        OpportunityStage? stage = OpportunityStage.InFollowUp, LossReason? loss = null,
        ClassifierProvider provider = ClassifierProvider.Rules, bool fallback = false)
        => new(service, reason, objection, stage, loss, 0.7m, "outcome.in_follow_up", provider, fallback);

    // ---- Opportunity: defaults and producers ------------------------------------------------------------------------

    [Fact]
    public void ANewOpportunity_HasAutoSources_AndNoProducerYet()
    {
        var opportunity = NewOpportunity();

        Assert.Equal((FieldSource.Auto, FieldSource.Auto, FieldSource.Auto), (opportunity.ServiceInterestSource, opportunity.InterestReasonSource, opportunity.ObjectionSource));
        Assert.Equal(((ClassifierProvider?)null, (ClassifierProvider?)null, (ClassifierProvider?)null, (ClassifierProvider?)null),
            (opportunity.ServiceInterestClassifiedBy, opportunity.InterestReasonClassifiedBy, opportunity.ObjectionClassifiedBy, opportunity.ClassifiedBy));
    }

    [Fact]
    public void AnUpdateWithoutAProvider_IsARulesReading_AsBefore()
    {
        var opportunity = NewOpportunity();

        opportunity.ApplyClassification(new ClassificationUpdate("axilas", null, ObjectionType.None, OpportunityStage.InFollowUp, null, 0.5m, null), T0);

        Assert.Equal(ClassifierProvider.Rules, opportunity.ServiceInterestClassifiedBy);
        Assert.Equal(ClassifierProvider.Rules, opportunity.ClassifiedBy);
    }

    [Fact]
    public void EachAutoWrite_RecordsItsProducer_PerField()
    {
        var opportunity = NewOpportunity();

        opportunity.ApplyClassification(Reading(provider: ClassifierProvider.Llm), T0);

        Assert.Equal((ClassifierProvider.Llm, ClassifierProvider.Llm, ClassifierProvider.Llm, ClassifierProvider.Llm),
            (opportunity.ServiceInterestClassifiedBy, opportunity.InterestReasonClassifiedBy, opportunity.ObjectionClassifiedBy, opportunity.ClassifiedBy));
    }

    [Fact]
    public void AnUndeterminedStage_DoesNotChangeWhoProducedTheStage()
    {
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(provider: ClassifierProvider.Llm), T0);

        opportunity.ApplyClassification(Reading(stage: null), T0.AddHours(1));

        Assert.Equal(ClassifierProvider.Llm, opportunity.ClassifiedBy);
        Assert.Equal(ClassifierProvider.Rules, opportunity.ServiceInterestClassifiedBy);
    }

    [Fact]
    public void AnLlmReadingThatSaysItIsARulesFallback_IsRejected()
    {
        var opportunity = NewOpportunity();

        Assert.Throws<DomainException>(() => opportunity.ApplyClassification(Reading(provider: ClassifierProvider.Llm, fallback: true), T0));
        Assert.Null(opportunity.ClassifiedAt);
    }

    // ---- CC-7: manual fields survive -------------------------------------------------------------------------------

    [Fact]
    public void AManualObjection_IsNeverOverwritten_ByAnyReading()
    {
        var opportunity = NewOpportunity();
        opportunity.SetManualObjection(ObjectionType.Trust, T0, User);

        opportunity.ApplyClassification(Reading(objection: ObjectionType.Price, provider: ClassifierProvider.Llm), T0.AddHours(1));
        opportunity.ApplyClassification(Reading(objection: ObjectionType.Pain), T0.AddHours(2));

        Assert.Equal((ObjectionType.Trust, FieldSource.Manual), (opportunity.Objection, opportunity.ObjectionSource));
        Assert.Equal(((DateTime?)T0, (Guid?)User), (opportunity.DetailsChangedAt, opportunity.DetailsChangedBy));
    }

    [Fact]
    public void AManuallyClearedField_StaysEmpty()
    {
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(), T0);
        opportunity.SetManualServiceInterest(null, T0.AddMinutes(1), User);

        opportunity.ApplyClassification(Reading(service: "piernas"), T0.AddHours(1));

        Assert.Equal(((string?)null, FieldSource.Manual), (opportunity.ServiceInterest, opportunity.ServiceInterestSource));
        Assert.Equal("estetica", opportunity.InterestReason);
    }

    [Fact]
    public void SavingAFieldUnchanged_PinsItManual()
    {
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(), T0);

        opportunity.SetManualInterestReason("estetica", T0.AddMinutes(1), User);
        opportunity.ApplyClassification(Reading(reason: "salud"), T0.AddHours(1));

        Assert.Equal(("estetica", FieldSource.Manual), (opportunity.InterestReason, opportunity.InterestReasonSource));
    }

    [Fact]
    public void ManualCodes_AreCheckedForShape()
    {
        var opportunity = NewOpportunity();

        Assert.Throws<DomainException>(() => opportunity.SetManualServiceInterest("Axilas y piernas", T0, User));
        Assert.Throws<DomainException>(() => opportunity.SetManualObjection((ObjectionType)99, T0, User));
        Assert.Equal(FieldSource.Auto, opportunity.ServiceInterestSource);
    }

    [Fact]
    public void RevertToAuto_KeepsTheValue_AndLetsTheNextReadingOverwriteIt()
    {
        var opportunity = NewOpportunity();
        opportunity.SetManualServiceInterest("cara", T0, User);

        opportunity.RevertDetailToAuto(OpportunityDetailField.ServiceInterest, T0.AddMinutes(1), User);
        Assert.Equal(("cara", FieldSource.Auto), (opportunity.ServiceInterest, opportunity.ServiceInterestSource));

        opportunity.ApplyClassification(Reading(service: "piernas"), T0.AddHours(1));
        Assert.Equal("piernas", opportunity.ServiceInterest);
    }

    // ---- CC-7: the explicit rules fallback protects LLM-sourced values -----------------------------------------------

    [Fact]
    public void AnLlmValue_IsReplacedByALaterLlmReading()
    {
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(service: "axilas", provider: ClassifierProvider.Llm), T0);

        opportunity.ApplyClassification(Reading(service: "piernas", provider: ClassifierProvider.Llm), T0.AddHours(1));

        Assert.Equal(("piernas", FieldSource.Auto, (ClassifierProvider?)ClassifierProvider.Llm),
            (opportunity.ServiceInterest, opportunity.ServiceInterestSource, opportunity.ServiceInterestClassifiedBy));
    }

    [Theory]
    [InlineData("piernas")]
    [InlineData(null)]
    public void AnExplicitRulesFallback_NeverOverwritesANonEmptyLlmValue(string? fallbackService)
    {
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(service: "axilas", reason: "estetica", objection: ObjectionType.Price, provider: ClassifierProvider.Llm), T0);

        opportunity.ApplyClassification(Reading(service: fallbackService, reason: null, objection: ObjectionType.None, fallback: true), T0.AddHours(1));

        Assert.Equal(("axilas", "estetica", ObjectionType.Price), (opportunity.ServiceInterest, opportunity.InterestReason, opportunity.Objection));
        Assert.Equal(ClassifierProvider.Llm, opportunity.ServiceInterestClassifiedBy);
    }

    [Fact]
    public void AnExplicitRulesFallback_StillFillsFieldsTheLlmLeftEmpty_AndAppliesTheStage()
    {
        // CC-7 S9, the accepted mix: stage, loss reason and confidence come from the rules, LLM-sourced descriptive fields are kept.
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(service: "axilas", reason: null, objection: ObjectionType.None, provider: ClassifierProvider.Llm), T0);

        opportunity.ApplyClassification(Reading(service: "piernas", reason: "salud", objection: ObjectionType.Price, stage: OpportunityStage.Lost, loss: LossReason.Price, fallback: true), T0.AddHours(1));

        Assert.Equal(("axilas", "salud", ObjectionType.Price), (opportunity.ServiceInterest, opportunity.InterestReason, opportunity.Objection));
        Assert.Equal((OpportunityStage.Lost, (LossReason?)LossReason.Price, (ClassifierProvider?)ClassifierProvider.Rules), (opportunity.Stage, opportunity.LossReason, opportunity.ClassifiedBy));
        Assert.Equal((ClassifierProvider?)ClassifierProvider.Rules, opportunity.InterestReasonClassifiedBy);
    }

    [Fact]
    public void APlainRulesRun_OverwritesLlmValues_SoARollbackLeavesNothingStale()
    {
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(service: "axilas", provider: ClassifierProvider.Llm), T0);

        opportunity.ApplyClassification(Reading(service: "piernas"), T0.AddHours(1));

        Assert.Equal(("piernas", (ClassifierProvider?)ClassifierProvider.Rules), (opportunity.ServiceInterest, opportunity.ServiceInterestClassifiedBy));
    }

    [Fact]
    public void TheFallbackProtectsOnlyTheFieldsTheLlmProduced()
    {
        // CC-7 S8: the LLM produced the service but no objection; a first fallback fills the objection (now produced by the rules).
        var opportunity = NewOpportunity();
        opportunity.ApplyClassification(Reading(service: "axilas", objection: ObjectionType.None, provider: ClassifierProvider.Llm), T0);
        opportunity.ApplyClassification(Reading(service: "piernas", objection: ObjectionType.Timing, fallback: true), T0.AddMinutes(1));
        Assert.Equal((ClassifierProvider.Llm, ClassifierProvider.Rules), (opportunity.ServiceInterestClassifiedBy, opportunity.ObjectionClassifiedBy));

        opportunity.ApplyClassification(Reading(service: "cara", objection: ObjectionType.Price, fallback: true), T0.AddHours(1));

        Assert.Equal("axilas", opportunity.ServiceInterest); // LLM-sourced, non-empty: protected
        Assert.Equal(ObjectionType.Price, opportunity.Objection); // produced by the rules: not protected
    }

    [Fact]
    public void ExistingRowsWithoutAProducer_AreNeverProtected()
    {
        var opportunity = NewOpportunity(); // producers are null, as on rows that existed before the column

        opportunity.ApplyClassification(Reading(service: "piernas", fallback: true), T0);

        Assert.Equal("piernas", opportunity.ServiceInterest);
    }

    // ---- Contact profile (CP-1..CP-4) -------------------------------------------------------------------------------

    private static Contact NewContact() => Contact.Create(Tenant, "573000000000", "Cliente de prueba", T0);

    [Fact]
    public void ANewContact_HasNoProfile()
    {
        var contact = NewContact();

        Assert.Equal(((AgeRange?)null, (FieldSource?)null, Gender.Undetermined, (FieldSource?)null, (ClassifierProvider?)null),
            (contact.AgeRange, contact.AgeRangeSource, contact.Gender, contact.GenderSource, contact.ProfileClassifiedBy));
    }

    [Fact]
    public void AnExtractedProfile_FillsTheDeterminedFields_AsAutoLlm()
    {
        var contact = NewContact();

        var changed = contact.ApplyExtractedProfile(AgeRange.From25To34, Gender.Female, T0);

        Assert.True(changed);
        Assert.Equal(((AgeRange?)AgeRange.From25To34, (FieldSource?)FieldSource.Auto, Gender.Female, (FieldSource?)FieldSource.Auto, (ClassifierProvider?)ClassifierProvider.Llm),
            (contact.AgeRange, contact.AgeRangeSource, contact.Gender, contact.GenderSource, contact.ProfileClassifiedBy));
        Assert.Equal((DateTime?)T0, contact.ProfileChangedAt);
        Assert.Null(contact.ProfileChangedBy);
    }

    [Fact]
    public void AnExtractedProfile_NeverErasesWhatIsKnown()
    {
        var contact = NewContact();
        contact.ApplyExtractedProfile(AgeRange.From25To34, Gender.Male, T0);

        var changed = contact.ApplyExtractedProfile(null, Gender.Undetermined, T0.AddHours(1));

        Assert.False(changed);
        Assert.Equal(((AgeRange?)AgeRange.From25To34, Gender.Male, (DateTime?)T0), (contact.AgeRange, contact.Gender, contact.ProfileChangedAt));
    }

    [Fact]
    public void ALaterStatement_UpdatesAnAutoValue()
    {
        var contact = NewContact();
        contact.ApplyExtractedProfile(AgeRange.From25To34, Gender.Undetermined, T0);

        contact.ApplyExtractedProfile(AgeRange.From35To44, Gender.Undetermined, T0.AddHours(1));

        Assert.Equal(AgeRange.From35To44, contact.AgeRange);
    }

    [Fact]
    public void ManualProfileFields_AreNeverOverwritten_EvenUndetermined()
    {
        var contact = NewContact();
        contact.SetManualAgeRange(AgeRange.From35To44, T0, User);
        contact.SetManualGender(Gender.Undetermined, T0, User);

        var changed = contact.ApplyExtractedProfile(AgeRange.From25To34, Gender.Female, T0.AddHours(1));

        Assert.False(changed);
        Assert.Equal(((AgeRange?)AgeRange.From35To44, (FieldSource?)FieldSource.Manual, Gender.Undetermined, (FieldSource?)FieldSource.Manual),
            (contact.AgeRange, contact.AgeRangeSource, contact.Gender, contact.GenderSource));
        Assert.Equal(((DateTime?)T0, (Guid?)User), (contact.ProfileChangedAt, contact.ProfileChangedBy));
    }

    [Fact]
    public void RevertingAProfileFieldToAuto_KeepsItsValue_AndLetsExtractionChangeIt()
    {
        var contact = NewContact();
        contact.SetManualGender(Gender.Female, T0, User);

        contact.RevertProfileFieldToAuto(ProfileField.Gender, T0.AddMinutes(1), User);
        Assert.Equal((Gender.Female, (FieldSource?)FieldSource.Auto), (contact.Gender, contact.GenderSource));

        contact.ApplyExtractedProfile(null, Gender.Other, T0.AddHours(1));
        Assert.Equal(Gender.Other, contact.Gender);
    }

    [Fact]
    public void UndefinedProfileValues_AreRejected()
    {
        var contact = NewContact();

        Assert.Throws<DomainException>(() => contact.ApplyExtractedProfile((AgeRange)42, Gender.Undetermined, T0));
        Assert.Throws<DomainException>(() => contact.SetManualGender((Gender)42, T0, User));
        Assert.Null(contact.AgeRange);
    }

    // ---- Conversation: LLM failures and the fallback completion paths (CC-6) ------------------------------------------

    private static Conversation NewConversation()
        => Conversation.Start(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0, ConversationSource.Organic);

    [Fact]
    public void RequestClassification_ResetsTheLlmFailures()
    {
        var conversation = NewConversation();
        conversation.RequestClassification(T0);
        conversation.CompleteWithRulesFallback(T0.AddMinutes(1), countAsLlmFailure: true, maxFallbackRetries: 2);
        Assert.Equal(1, conversation.LlmFailures);

        conversation.RequestClassification(T0.AddMinutes(2));

        Assert.Equal(0, conversation.LlmFailures);
    }

    [Fact]
    public void ACountedFallback_KeepsTheConversationDirty_UntilTheRetriesAreSpent()
    {
        // CC-6 S5: MaxFallbackRetries = 2 means one attempt and two retries; the third counted failure clears the flag with the rules standing.
        var conversation = NewConversation();
        conversation.RequestClassification(T0);

        Assert.True(conversation.CompleteWithRulesFallback(T0.AddMinutes(1), countAsLlmFailure: true, maxFallbackRetries: 2));
        Assert.True(conversation.CompleteWithRulesFallback(T0.AddMinutes(2), countAsLlmFailure: true, maxFallbackRetries: 2));
        Assert.Equal((true, 2, (DateTime?)T0.AddMinutes(2)), (conversation.ClassificationDirty, conversation.LlmFailures, conversation.ClassificationAttemptedAt));

        Assert.False(conversation.CompleteWithRulesFallback(T0.AddMinutes(3), countAsLlmFailure: true, maxFallbackRetries: 2));
        Assert.Equal((false, 3, 0), (conversation.ClassificationDirty, conversation.LlmFailures, conversation.ClassificationFailures));
    }

    [Fact]
    public void AnUncountedFallback_KeepsItDirty_WithoutCountingAnything()
    {
        // A transport failure or an open breaker: the attempt time is set (it may be in the future, to defer the claim), nothing is counted.
        var conversation = NewConversation();
        conversation.RequestClassification(T0);

        var dirty = conversation.CompleteWithRulesFallback(T0.AddMinutes(5), countAsLlmFailure: false, maxFallbackRetries: 2);

        Assert.True(dirty);
        Assert.Equal((true, 0, 0, (DateTime?)T0.AddMinutes(5)), (conversation.ClassificationDirty, conversation.LlmFailures, conversation.ClassificationFailures, conversation.ClassificationAttemptedAt));
    }

    [Fact]
    public void ANegativeRetryCount_IsRejected()
        => Assert.Throws<DomainException>(() => NewConversation().CompleteWithRulesFallback(T0, countAsLlmFailure: true, maxFallbackRetries: -1));

    [Fact]
    public void AnLlmSuccess_ClearsTheFlag_AndResetsTheLlmFailures()
    {
        var conversation = NewConversation();
        conversation.RequestClassification(T0);
        conversation.CompleteWithRulesFallback(T0.AddMinutes(1), countAsLlmFailure: true, maxFallbackRetries: 2);

        conversation.CompleteLlmClassification(T0.AddMinutes(2));

        Assert.Equal((false, 0, (DateTime?)T0.AddMinutes(2)), (conversation.ClassificationDirty, conversation.LlmFailures, conversation.ClassificationAttemptedAt));
    }

    [Fact]
    public void APlainCompletion_LeavesTheLlmFailuresAlone()
    {
        // AiEnabled off, quota, caps, Provider=Rules: the rules decide and nothing about the LLM is counted or reset.
        var conversation = NewConversation();
        conversation.RequestClassification(T0);
        conversation.CompleteWithRulesFallback(T0.AddMinutes(1), countAsLlmFailure: true, maxFallbackRetries: 2);

        conversation.CompleteClassification(T0.AddMinutes(2));

        Assert.Equal((false, 1), (conversation.ClassificationDirty, conversation.LlmFailures));
    }

    [Fact]
    public void Requeue_MarksItDirtyAgain_WithoutResettingAnyCounter()
    {
        // The profile write after the commit failed: the next run retries, and the attempt time keeps the retry delay.
        var conversation = NewConversation();
        conversation.RequestClassification(T0);
        conversation.CompleteWithRulesFallback(T0.AddMinutes(1), countAsLlmFailure: true, maxFallbackRetries: 0);
        Assert.False(conversation.ClassificationDirty);

        conversation.RequeueClassification();

        Assert.Equal((true, 1, (DateTime?)T0.AddMinutes(1), (DateTime?)T0), (conversation.ClassificationDirty, conversation.LlmFailures, conversation.ClassificationAttemptedAt, conversation.ClassificationRequestedAt));
    }

    // ---- Conversation: the snapshot hash of the LLM result that is applied (call reduction) ---------------------------

    private static readonly string HashA = new('a', 64);

    [Fact]
    public void AnLlmSuccess_RemembersTheSnapshotHashOfTheResultItApplied()
    {
        var conversation = NewConversation();
        conversation.RequestClassification(T0);

        conversation.CompleteLlmClassification(T0.AddMinutes(1), HashA);

        Assert.Equal(HashA, conversation.LastLlmSnapshotHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void ASnapshotHash_IsSixtyFourLowerCaseHexCharacters(string hash)
        => Assert.Throws<DomainException>(() => NewConversation().CompleteLlmClassification(T0, hash));

    [Fact]
    public void EveryOtherCompletion_ForgetsTheHash_BecauseTheAppliedResultIsNoLongerThatLlmReading()
    {
        // A rules result (plain or fallback) or a requeue replaces or invalidates what the hash stood for: the next claim must not skip the call.
        var plain = NewConversation();
        plain.CompleteLlmClassification(T0, HashA);
        plain.CompleteClassification(T0.AddMinutes(1));

        var fallback = NewConversation();
        fallback.CompleteLlmClassification(T0, HashA);
        fallback.CompleteWithRulesFallback(T0.AddMinutes(1), countAsLlmFailure: false, maxFallbackRetries: 2);

        var requeued = NewConversation();
        requeued.CompleteLlmClassification(T0, HashA);
        requeued.RequeueClassification();

        Assert.Equal(((string?)null, (string?)null, (string?)null), (plain.LastLlmSnapshotHash, fallback.LastLlmSnapshotHash, requeued.LastLlmSnapshotHash));
    }

    // ---- Conversation: a contact profile pending to be written after the commit (crash safety) -----------------------

    [Fact]
    public void APendingProfile_IsRecordedWithItsValues_AndClearedOnceWritten()
    {
        var conversation = NewConversation();

        conversation.RecordPendingProfile(AgeRange.From25To34, Gender.Female);
        Assert.Equal((true, (AgeRange?)AgeRange.From25To34, (Gender?)Gender.Female), (conversation.ProfilePending, conversation.PendingAgeRange, conversation.PendingGender));

        conversation.ClearPendingProfile();
        Assert.Equal((false, (AgeRange?)null, (Gender?)null), (conversation.ProfilePending, conversation.PendingAgeRange, conversation.PendingGender));
    }

    [Fact]
    public void APendingProfile_SurvivesANewMessageAndAnotherCompletion()
    {
        // It is the reading the LLM already made; only writing it clears it (a later reading replaces it).
        var conversation = NewConversation();
        conversation.RecordPendingProfile(null, Gender.Male);

        conversation.RequestClassification(T0.AddMinutes(1));
        conversation.CompleteWithRulesFallback(T0.AddMinutes(2), countAsLlmFailure: false, maxFallbackRetries: 2);

        Assert.Equal((true, (Gender?)Gender.Male), (conversation.ProfilePending, conversation.PendingGender));
    }

    [Fact]
    public void AnEmptyOrUndefinedPendingProfile_IsRejected()
    {
        Assert.Throws<DomainException>(() => NewConversation().RecordPendingProfile(null, Gender.Undetermined));
        Assert.Throws<DomainException>(() => NewConversation().RecordPendingProfile((AgeRange)42, Gender.Female));
    }

    [Fact]
    public void ANewMessage_KeepsTheHash_SinceTheHashCoversTheMessages()
    {
        // A new message changes the hash of the next snapshot anyway; keeping the stored one costs nothing and lets an unchanged re-request skip the call.
        var conversation = NewConversation();
        conversation.CompleteLlmClassification(T0, HashA);

        conversation.RequestClassification(T0.AddMinutes(1));

        Assert.Equal(HashA, conversation.LastLlmSnapshotHash);
    }
}
