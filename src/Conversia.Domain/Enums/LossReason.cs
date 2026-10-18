namespace Conversia.Domain.Enums;

/// <summary>Why a sales opportunity was lost. Stored by name.</summary>
public enum LossReason
{
    /// <summary>Lost, reason not known.</summary>
    Unknown = 0,

    Price = 1,

    /// <summary>The service or the schedule the customer needs is not available.</summary>
    Availability = 2,

    /// <summary>The branch is too far or not where the customer is.</summary>
    Location = 3,

    /// <summary>Not now: the customer postponed and did not come back.</summary>
    Timing = 4,

    /// <summary>The customer went with another provider.</summary>
    Competitor = 5,

    /// <summary>The customer stopped answering (set when a conversation closes by inactivity).</summary>
    NoResponse = 6,

    /// <summary>The customer said they are not interested.</summary>
    NotInterested = 7,

    Other = 8,
}
