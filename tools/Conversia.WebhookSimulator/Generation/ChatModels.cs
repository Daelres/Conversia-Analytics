namespace Conversia.WebhookSimulator.Generation;

/// <summary>Who wrote a message, from the business point of view.</summary>
public enum Speaker
{
    Customer,
    Business,
}

/// <summary>How the rule-based classifier is expected to read a chat once it is quiet (the stage the dashboard will count it under).</summary>
public enum ExpectedOutcome
{
    InFollowUp,
    Won,
    Lost,
}

/// <summary>The shapes of sales chat the simulator can tell. Each one is written to be read the same way every time by the rule-based classifier.</summary>
public enum ChatKind
{
    /// <summary>The customer asks to book, the advisor proposes a day and an hour, and the advisor confirms: Won.</summary>
    Booked,

    /// <summary>A price objection solved with a promotion, then the booking is confirmed by the advisor: Won.</summary>
    BookedAfterPriceObjection,

    /// <summary>The advisor proposes a day and an hour and the customer says yes: Won.</summary>
    BookedByCustomerYes,

    /// <summary>Too expensive, no thanks: Lost (price).</summary>
    LostPrice,

    /// <summary>The customer already booked somewhere else: Lost (competitor).</summary>
    LostCompetitor,

    /// <summary>The customer is not interested: Lost.</summary>
    LostNotInterested,

    /// <summary>"Let me think about it": in follow-up (timing objection).</summary>
    FollowUpThinking,

    /// <summary>Asks whether it hurts: in follow-up (pain objection).</summary>
    FollowUpPain,

    /// <summary>Asks for information, the advisor answers and the customer never writes again: in follow-up (the closing job turns it into a lost sale later).</summary>
    FollowUpSilent,

    /// <summary>The customer asks and nobody ever answers: in follow-up, and the one that makes the "unanswered" alerts.</summary>
    Unanswered,
}

/// <param name="OffsetSeconds">Seconds after the start of the conversation (the first customer message is at 0).</param>
public sealed record ScriptedMessage(Speaker Speaker, string Text, int OffsetSeconds);

/// <param name="Zone">The body zone the customer asked about, in the words of the chat.</param>
public sealed record ChatScript(ChatKind Kind, ExpectedOutcome Expected, string Zone, IReadOnlyList<ScriptedMessage> Messages)
{
    public bool HasBusinessReply => Messages.Any(m => m.Speaker == Speaker.Business);
}

/// <param name="CustomerFirstName">Used by the advisor's greeting.</param>
/// <param name="BranchName">Named in the booking confirmation.</param>
/// <param name="FirstReplySeconds">How long the business takes to answer the first message (ignored by chats nobody answers).</param>
/// <param name="FromAd">The customer wrote because of a click-to-WhatsApp ad: the opening line says so.</param>
public sealed record ChatContext(string CustomerFirstName, string BranchName, int FirstReplySeconds, bool FromAd = false);
