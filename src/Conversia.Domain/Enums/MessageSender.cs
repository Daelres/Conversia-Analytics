namespace Conversia.Domain.Enums;

/// <summary>Who sent a message, as far as the system can tell (docs/MESSAGING.md, "Who sent it"). Stored by name.</summary>
public enum MessageSender
{
    /// <summary>Not recorded (messages stored before the column existed).</summary>
    Unknown = 0,

    /// <summary>The customer (every inbound message).</summary>
    Customer = 1,

    /// <summary>A platform user, from Conversia (the send path of docs/MESSAGING.md).</summary>
    Conversia = 2,

    /// <summary>The business, from the WhatsApp Business app or another device: a message the webhook delivered as an echo.</summary>
    PhoneApp = 3,

    /// <summary>Reserved for the AI sales assistant (roadmap stage 5).</summary>
    AiAssistant = 4,
}
