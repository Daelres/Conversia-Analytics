namespace Conversia.Domain.Enums;

/// <summary>Position of an opportunity in the sales funnel. Stored by name.</summary>
public enum OpportunityStage
{
    New = 0,
    InFollowUp = 1,
    Won = 2,
    Lost = 3,
}
