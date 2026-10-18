using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Services;
using Conversia.Application.Time;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Tests.Shared;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>What <see cref="ConversationService"/> asks of its collaborators and refuses on its own. Tenant isolation is the query filter's job (integration tests).</summary>
public class ConversationServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 15, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IConversationQueries> _queries = new();
    private readonly Mock<IConversationRepository> _conversations = new();
    private readonly Mock<IAdvisorRepository> _advisors = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _branch = Guid.NewGuid();

    private ConversationService CreateSut()
        => new(_queries.Object, _conversations.Object, _advisors.Object, new TenantTimeZone("America/Bogota"), new FixedTimeProvider(Now));

    private Conversation StoredConversation()
    {
        var conversation = Conversation.Start(_tenant, Guid.NewGuid(), Guid.NewGuid(), _branch, Now, ConversationSource.Organic);
        _conversations.Setup(r => r.GetForUpdateAsync(conversation.Id, It.IsAny<CancellationToken>())).ReturnsAsync(conversation);
        _queries.Setup(q => q.GetSummaryAsync(conversation.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ConversationListItem { Id = conversation.Id });
        return conversation;
    }

    private Advisor StoredAdvisor(Guid? branchId = null)
    {
        var advisor = Advisor.Create(_tenant, branchId ?? _branch, "Ana");
        _advisors.Setup(r => r.GetByIdAsync(advisor.Id, It.IsAny<CancellationToken>())).ReturnsAsync(advisor);
        return advisor;
    }

    // ---- List ---------------------------------------------------------------------------------

    [Fact]
    public async Task List_ParsesTheFiltersAndTurnsLocalDatesIntoUtcInstants()
    {
        ConversationFilter? seen = null;
        _queries.Setup(q => q.ListAsync(It.IsAny<ConversationFilter>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationFilter, CancellationToken>((f, _) => seen = f)
            .ReturnsAsync(([], 0));
        var advisorId = Guid.NewGuid();

        var page = await CreateSut().ListAsync(new ConversationQuery
        {
            Page = 3,
            PageSize = 50,
            AdvisorId = advisorId,
            Stage = "lost",
            Source = "Ads",
            Status = "Closed",
            From = new DateOnly(2026, 10, 1),
            To = new DateOnly(2026, 11, 1),
            Unanswered = true,
        });

        Assert.Equal((3, 50, 0), (page.Page, page.PageSize, page.TotalCount));
        Assert.Equal(
            new ConversationFilter(
                3, 50, null, advisorId, false, OpportunityStage.Lost, ConversationSource.Ads, ConversationStatus.Closed,
                new DateTime(2026, 10, 1, 5, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc), true),
            seen);
    }

    [Fact]
    public async Task List_WithoutFilters_AsksForEverything()
    {
        ConversationFilter? seen = null;
        _queries.Setup(q => q.ListAsync(It.IsAny<ConversationFilter>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationFilter, CancellationToken>((f, _) => seen = f)
            .ReturnsAsync(([], 0));

        await CreateSut().ListAsync(new ConversationQuery());

        Assert.Equal(new ConversationFilter(1, 20), seen);
    }

    // ---- Get ----------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ThrowsNotFoundWhenThereIsNoSuchConversation()
        => await Assert.ThrowsAsync<ConversationNotFoundException>(() => CreateSut().GetAsync(Guid.NewGuid()));

    // ---- Advisor ------------------------------------------------------------------------------

    [Fact]
    public async Task AssignAdvisor_SavesTheAdvisorOfTheConversationsBranch()
    {
        var conversation = StoredConversation();
        var advisor = StoredAdvisor();

        await CreateSut().AssignAdvisorAsync(conversation.Id, new AssignAdvisorRequest { AdvisorId = advisor.Id });

        Assert.Equal(advisor.Id, conversation.AdvisorId);
        _conversations.Verify(r => r.UpdateAsync(conversation, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AssignAdvisor_OfAnotherBranch_IsRefusedInSpanish_AndSavesNothing()
    {
        var conversation = StoredConversation();
        var advisor = StoredAdvisor(branchId: Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => CreateSut().AssignAdvisorAsync(conversation.Id, new AssignAdvisorRequest { AdvisorId = advisor.Id }));

        Assert.Equal(ConversationService.AdvisorOtherBranchMessage, ex.Message);
        Assert.Null(conversation.AdvisorId);
        _conversations.Verify(r => r.UpdateAsync(It.IsAny<Conversation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AssignAdvisor_UnknownOrOtherTenantAdvisor_IsRefused()
    {
        var conversation = StoredConversation();

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => CreateSut().AssignAdvisorAsync(conversation.Id, new AssignAdvisorRequest { AdvisorId = Guid.NewGuid() }));

        Assert.Equal(ConversationService.UnknownAdvisorMessage, ex.Message);
    }

    [Fact]
    public async Task AssignAdvisor_NullUnassigns()
    {
        var conversation = StoredConversation();
        conversation.AssignAdvisor(Guid.NewGuid());

        await CreateSut().AssignAdvisorAsync(conversation.Id, new AssignAdvisorRequest { AdvisorId = null });

        Assert.Null(conversation.AdvisorId);
        _conversations.Verify(r => r.UpdateAsync(conversation, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AssignAdvisor_ToAMissingConversation_IsNotFound()
        => await Assert.ThrowsAsync<ConversationNotFoundException>(
            () => CreateSut().AssignAdvisorAsync(Guid.NewGuid(), new AssignAdvisorRequest { AdvisorId = Guid.NewGuid() }));

    // ---- Outcome ------------------------------------------------------------------------------

    private Opportunity CaptureOutcomeChange(Guid conversationId)
    {
        var opportunity = Opportunity.Open(_tenant, conversationId);
        var conversation = Conversation.Start(_tenant, Guid.NewGuid(), Guid.NewGuid(), _branch, Now, ConversationSource.Organic);
        _conversations
            .Setup(r => r.ChangeOpportunityAsync(conversationId, It.IsAny<Action<Conversation, Opportunity>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Action<Conversation, Opportunity>, CancellationToken>((_, change, _) => change(conversation, opportunity))
            .ReturnsAsync(opportunity);
        return opportunity;
    }

    [Fact]
    public async Task SetOutcome_AppliesAManualLossAtTheCurrentTime()
    {
        var id = Guid.NewGuid();
        var opportunity = CaptureOutcomeChange(id);

        var response = await CreateSut().SetOutcomeAsync(id, new SetOpportunityOutcomeRequest { Stage = "Lost", LossReason = "price", Detail = "caro" });

        Assert.Equal((OpportunityStage.Lost, LossReason.Price, StageSource.Manual, Now), (opportunity.Stage, opportunity.LossReason, opportunity.StageSource, opportunity.ClosedAt!.Value));
        Assert.Equal(("Lost", "Manual", "Price", "caro"), (response.Stage, response.StageSource, response.LossReason, response.LossDetail));
    }

    [Fact]
    public async Task SetOutcome_LostWithoutAReason_IsRefused()
    {
        var id = Guid.NewGuid();
        CaptureOutcomeChange(id);

        await Assert.ThrowsAsync<DomainException>(() => CreateSut().SetOutcomeAsync(id, new SetOpportunityOutcomeRequest { Stage = "Lost" }));
    }

    [Fact]
    public async Task SetOutcome_OnAMissingConversation_IsNotFound()
        => await Assert.ThrowsAsync<ConversationNotFoundException>(
            () => CreateSut().SetOutcomeAsync(Guid.NewGuid(), new SetOpportunityOutcomeRequest { Stage = "Won" }));
}
