using Conversia.Application.Classification;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification;

/// <summary>The members added for the LLM provider are trailing and optional: every existing construction keeps its meaning.</summary>
public class ClassificationModelsTests
{
    [Fact]
    public void AResultBuiltTheOldWay_HasNoProfile_NoEvidence_AndIsNotAFallback()
    {
        var result = new ClassificationResult("axilas", null, ObjectionType.None, ClassificationOutcome.InFollowUp, null, 0.6m, ["outcome.in_follow_up"]);

        Assert.Equal(((AgeRange?)null, Gender.Undetermined, (int?)null, false), (result.AgeRange, result.Gender, result.WonEvidenceIndex, result.IsRulesFallback));
    }

    [Fact]
    public void ASnapshotBuiltTheOldWay_KnowsNoNamesOrNumbers_AndItsMessagesHaveNoId()
    {
        var message = new SnapshotMessage(MessageDirection.Inbound, "text", "Hola", DateTime.UnixEpoch);
        var snapshot = new ConversationSnapshot(Guid.NewGuid(), "Sede", DateTime.UnixEpoch, null, null, [message]);

        Assert.Null(message.Id);
        Assert.Equal((Guid.Empty, 0, 0, (string?)null, (string?)null),
            (snapshot.TenantId, snapshot.Names.Count, snapshot.Numbers.Count, snapshot.ContactName, snapshot.AdvisorName));
    }

    [Fact]
    public void TheKnownValues_AreReadThroughNonNullAccessors()
    {
        var snapshot = new ConversationSnapshot(Guid.NewGuid(), "Sede", DateTime.UnixEpoch, null, null, [], Guid.NewGuid(), ["Ana"], ["573001112233"]);

        Assert.Equal(["Ana"], snapshot.Names);
        Assert.Equal(["573001112233"], snapshot.Numbers);
    }
}
