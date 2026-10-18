using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Conversations;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>The DataAnnotations of the conversation requests, run the way MVC runs them.</summary>
public class ConversationRequestValidationTests
{
    private static List<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }

    private static bool Fails(object instance, string member) => Validate(instance).Any(r => r.MemberNames.Contains(member));

    // ---- ConversationQuery --------------------------------------------------------------------

    [Fact]
    public void TheDefaultQuery_IsValid()
        => Assert.Empty(Validate(new ConversationQuery()));

    [Theory]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    [InlineData(-5, false)]
    public void ThePageSize_IsBetween1And100(int pageSize, bool valid)
        => Assert.Equal(!valid, Fails(new ConversationQuery { PageSize = pageSize }, nameof(ConversationQuery.PageSize)));

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void ThePage_StartsAtOne(int page, bool valid)
        => Assert.Equal(!valid, Fails(new ConversationQuery { Page = page }, nameof(ConversationQuery.Page)));

    [Theory]
    [InlineData("Won", true)]
    [InlineData("won", true)]
    [InlineData("InFollowUp", true)]
    [InlineData("", true)]
    [InlineData("2", false)] // a number in text names no member
    [InlineData("1,2", false)] // nor does a comma list
    [InlineData("Almost", false)]
    public void TheStageFilter_IsAMemberName(string stage, bool valid)
        => Assert.Equal(!valid, Fails(new ConversationQuery { Stage = stage }, nameof(ConversationQuery.Stage)));

    [Theory]
    [InlineData("Ads", true)]
    [InlineData("Radio", false)]
    public void TheSourceFilter_IsAMemberName(string source, bool valid)
        => Assert.Equal(!valid, Fails(new ConversationQuery { Source = source }, nameof(ConversationQuery.Source)));

    [Theory]
    [InlineData("Open", true)]
    [InlineData("Archived", false)]
    public void TheStatusFilter_IsAMemberName(string status, bool valid)
        => Assert.Equal(!valid, Fails(new ConversationQuery { Status = status }, nameof(ConversationQuery.Status)));

    [Fact]
    public void TheDateRange_MustStartBeforeItEnds()
    {
        var day = new DateOnly(2026, 10, 18);

        Assert.Empty(Validate(new ConversationQuery { From = day, To = day.AddDays(1) }));
        Assert.Empty(Validate(new ConversationQuery { From = day }));
        Assert.NotEmpty(Validate(new ConversationQuery { From = day, To = day }));
        Assert.NotEmpty(Validate(new ConversationQuery { From = day, To = day.AddDays(-1) }));
    }

    [Fact]
    public void AnAdvisor_AndUnassigned_CannotBeCombined()
    {
        Assert.NotEmpty(Validate(new ConversationQuery { AdvisorId = Guid.NewGuid(), Unassigned = true }));
        Assert.Empty(Validate(new ConversationQuery { AdvisorId = Guid.NewGuid(), Unassigned = false }));
        Assert.Empty(Validate(new ConversationQuery { Unassigned = true }));
    }

    [Fact]
    public void TheMessagesOfTheQueryValidation_AreSpanish()
    {
        var messages = Validate(new ConversationQuery { PageSize = 500, Stage = "x", From = new DateOnly(2026, 1, 2), To = new DateOnly(2026, 1, 1) })
            .Select(r => r.ErrorMessage!);

        Assert.All(messages, m => Assert.Matches("El campo|No se puede", m));
    }

    // ---- SetOpportunityOutcomeRequest ---------------------------------------------------------

    [Fact]
    public void TheOutcomeStage_IsRequired_AndAMemberName()
    {
        Assert.True(Fails(new SetOpportunityOutcomeRequest(), nameof(SetOpportunityOutcomeRequest.Stage)));
        Assert.True(Fails(new SetOpportunityOutcomeRequest { Stage = "3" }, nameof(SetOpportunityOutcomeRequest.Stage)));
        Assert.Empty(Validate(new SetOpportunityOutcomeRequest { Stage = "Won" }));
    }

    [Fact]
    public void TheLossReason_IsAMemberName()
    {
        Assert.Empty(Validate(new SetOpportunityOutcomeRequest { Stage = "Lost", LossReason = "NoResponse" }));
        Assert.True(Fails(new SetOpportunityOutcomeRequest { Stage = "Lost", LossReason = "8" }, nameof(SetOpportunityOutcomeRequest.LossReason)));
        Assert.True(Fails(new SetOpportunityOutcomeRequest { Stage = "Lost", LossReason = "Cheap" }, nameof(SetOpportunityOutcomeRequest.LossReason)));
    }

    [Fact]
    public void TheDetail_IsOptional_Within500Characters_WithoutNul()
    {
        Assert.Empty(Validate(new SetOpportunityOutcomeRequest { Stage = "Lost", LossReason = "Price", Detail = null }));
        Assert.Empty(Validate(new SetOpportunityOutcomeRequest { Stage = "Lost", LossReason = "Price", Detail = "   " }));
        Assert.Empty(Validate(new SetOpportunityOutcomeRequest { Stage = "Lost", LossReason = "Price", Detail = new string('a', 500) }));
        Assert.True(Fails(new SetOpportunityOutcomeRequest { Stage = "Lost", LossReason = "Price", Detail = new string('a', 501) }, nameof(SetOpportunityOutcomeRequest.Detail)));
        Assert.True(Fails(new SetOpportunityOutcomeRequest { Stage = "Lost", LossReason = "Price", Detail = "a\0b" }, nameof(SetOpportunityOutcomeRequest.Detail)));
    }
}
