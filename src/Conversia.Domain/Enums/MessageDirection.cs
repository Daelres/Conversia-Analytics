namespace Conversia.Domain.Enums;

/// <summary>Who wrote a message, from the business point of view. Stored by name.</summary>
public enum MessageDirection
{
    /// <summary>Sent by the customer.</summary>
    Inbound = 0,

    /// <summary>Sent by the business (an advisor, from the WhatsApp Business app or any other device).</summary>
    Outbound = 1,
}
