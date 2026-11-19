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

    /// <summary>The AI sales assistant (docs/ASSISTANT.md): the rules set it for a deal won in the chat whose business messages were all the assistant's. A person cannot set it.</summary>
    AiAssistant = 3,
}
