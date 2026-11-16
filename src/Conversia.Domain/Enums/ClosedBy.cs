namespace Conversia.Domain.Enums;

/// <summary>Who closed the deal. Stored by name.</summary>
public enum ClosedBy
{
    /// <summary>Not closed, or it cannot be told.</summary>
    Unknown = 0,

    /// <summary>An advisor of the business (in the chat, or after moving it off the chat).</summary>
    Advisor = 1,

    /// <summary>The customer booked or paid on their own (the web page, a payment link).</summary>
    CustomerSelfService = 2,

    /// <summary>Reserved for the AI sales assistant (roadmap stage 5). No classifier produces it and a person cannot set it yet.</summary>
    AiAssistant = 3,
}
