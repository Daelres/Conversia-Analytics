namespace Conversia.Domain.Enums;

/// <summary>
/// How far a sales conversation got: an ordered, closed list (a higher value is further down the funnel). The stage recorded is the
/// furthest one the conversation reached, whatever the order the steps happened in (docs/CLASSIFICATION.md, "Closing point"). Stored by name.
/// </summary>
public enum ReachedStage
{
    /// <summary>The customer wrote (anything, even a greeting).</summary>
    FirstContact = 1,

    /// <summary>The customer said what they want: a service or body zone, or why they are interested.</summary>
    NeedsIdentified = 2,

    /// <summary>The business gave a price (an amount of money in an advisor message).</summary>
    PriceShared = 3,

    /// <summary>The customer raised an objection (price, timing, location, trust, pain, other).</summary>
    ObjectionRaised = 4,

    /// <summary>A booking was proposed by the business or requested by the customer.</summary>
    AppointmentProposed = 5,

    /// <summary>The booking was confirmed (the rules' Won: the business confirmed it, or the customer accepted a concrete proposal).</summary>
    AppointmentBooked = 6,

    /// <summary>A payment or a deposit was made or confirmed.</summary>
    PaymentOrDeposit = 7,
}
