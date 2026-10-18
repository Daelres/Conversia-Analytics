using Conversia.Application.Classification;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Classification;

namespace Conversia.Infrastructure.UnitTests.Classification;

/// <summary>How a classifier's result becomes what an opportunity stores.</summary>
public class ClassificationMappingTests
{
    private static ClassificationResult Result(
        ClassificationOutcome outcome, LossReason? loss = null, IReadOnlyList<string>? codes = null, ObjectionType objection = ObjectionType.Price)
        => new("axilas", "estetica", objection, outcome, loss, 0.8m, codes ?? ["outcome.x"]);

    [Theory]
    [InlineData(ClassificationOutcome.InFollowUp, OpportunityStage.InFollowUp)]
    [InlineData(ClassificationOutcome.Won, OpportunityStage.Won)]
    [InlineData(ClassificationOutcome.Lost, OpportunityStage.Lost)]
    public void TheOutcome_BecomesTheStage(ClassificationOutcome outcome, OpportunityStage expected)
        => Assert.Equal(expected, ConversationClassificationRunner.ToUpdate(Result(outcome, outcome == ClassificationOutcome.Lost ? LossReason.Price : null)).Stage);

    [Fact]
    public void Undetermined_HasNoStage_SoTheStageIsLeftAlone()
        => Assert.Null(ConversationClassificationRunner.ToUpdate(Result(ClassificationOutcome.Undetermined)).Stage);

    [Fact]
    public void TheLossReason_TravelsOnlyWithLost()
    {
        Assert.Equal(LossReason.Price, ConversationClassificationRunner.ToUpdate(Result(ClassificationOutcome.Lost, LossReason.Price)).LossReason);
        Assert.Null(ConversationClassificationRunner.ToUpdate(Result(ClassificationOutcome.Won, LossReason.Price)).LossReason);
    }

    [Fact]
    public void TheDescriptiveFields_AreCopied()
    {
        var update = ConversationClassificationRunner.ToUpdate(Result(ClassificationOutcome.InFollowUp));

        Assert.Equal(("axilas", "estetica", ObjectionType.Price, 0.8m), (update.ServiceInterest, update.InterestReason, update.Objection, update.Confidence));
    }

    [Fact]
    public void TheRuleCodes_AreJoinedWithCommas()
        => Assert.Equal("a.b,c.d,e", ConversationClassificationRunner.ToUpdate(Result(ClassificationOutcome.InFollowUp, codes: ["a.b", "c.d", "e"])).RuleCodes);

    [Fact]
    public void TheRuleCodes_AreCutAtAWholeCode_WhenTheyDoNotFit()
    {
        var codes = Enumerable.Range(0, 40).Select(i => $"rule.number_{i:00}").ToList(); // 14 characters each
        var update = ConversationClassificationRunner.ToUpdate(Result(ClassificationOutcome.InFollowUp, codes: codes));

        Assert.True(update.RuleCodes!.Length <= Opportunity.ClassificationRulesMaxLength);
        Assert.All(update.RuleCodes.Split(','), code => Assert.Contains(code, codes));
        Assert.Equal(codes.Take(13), update.RuleCodes.Split(',')); // 13 * 14 + 12 commas = 194; a 14th would need 209
    }

    [Fact]
    public void NoCodes_MeanNull()
        => Assert.Null(ConversationClassificationRunner.ToUpdate(Result(ClassificationOutcome.InFollowUp, codes: [])).RuleCodes);
}

public class ClassificationErrorsTests
{
    [Fact]
    public void ADescription_HasTheTypesOfTheFailure_AndNeverItsMessage()
    {
        var failure = new InvalidOperationException("regex failed on 'quiero axilas' from 573009998877", new FormatException("Marcela Zuluaga"));

        var description = ClassificationErrors.Describe(failure);

        Assert.Equal("InvalidOperationException caused by FormatException", description);
    }

    [Fact]
    public void ALoneException_IsJustItsType()
        => Assert.Equal("TimeoutException", ClassificationErrors.Describe(new TimeoutException("secret text")));

    [Fact]
    public void APostgresFailure_AddsItsSqlState_ButNotItsText()
    {
        var failure = new InvalidOperationException("wrapper", new Npgsql.PostgresException("duplicate key value violates unique constraint (573009998877)", "ERROR", "ERROR", "23505"));

        var description = ClassificationErrors.Describe(failure);

        Assert.Equal("InvalidOperationException caused by PostgresException (SQLSTATE 23505)", description);
        Assert.DoesNotContain("573009998877", description);
    }
}
