using Conversia.Application.DTOs.Conversations;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>The opportunity of the conversation detail carries the closing point as codes (docs/CLASSIFICATION.md, "Closing point").</summary>
public class OpportunityResponseTests
{
    [Fact]
    public void TheClosingPoint_IsCarried_WithItsSources()
    {
        var opportunity = Opportunity.Open(Guid.NewGuid(), Guid.NewGuid());
        opportunity.ApplyClassification(new ClassificationUpdate(null, null, ObjectionType.None, OpportunityStage.InFollowUp, null, 0.5m, null,
            ClosingPoint: new ClosingPointReading(ReachedStage.PriceShared, OffChannelMove.PhoneCall, 4, ClosedBy.Unknown, true)), DateTime.UtcNow);
        opportunity.SetManualClosedBy(ClosedBy.Advisor, DateTime.UtcNow);

        var response = OpportunityResponse.From(opportunity);

        Assert.Equal(("PriceShared", "PhoneCall", (int?)4, "Advisor", true), (response.ReachedStage, response.MovedOffChannel, response.OffChannelEvidenceIndex, response.ClosedBy, response.ClosedOutsideSystem));
        Assert.Equal(("Auto", "Auto", "Manual", "Auto"), (response.ReachedStageSource, response.MovedOffChannelSource, response.ClosedBySource, response.ClosedOutsideSystemSource));
    }

    [Fact]
    public void ANewOpportunity_HasNoClosingPoint()
    {
        var response = OpportunityResponse.From(Opportunity.Open(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(((string?)null, "None", (int?)null, "Unknown", false), (response.ReachedStage, response.MovedOffChannel, response.OffChannelEvidenceIndex, response.ClosedBy, response.ClosedOutsideSystem));
    }
}
