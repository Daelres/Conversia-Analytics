namespace Conversia.Application.DTOs.Contacts;

/// <summary>What a contact suppression deleted (counts only: nothing of the contact is left to return).</summary>
public class ContactSuppressionResponse
{
    public Guid ContactId { get; set; }

    public int Conversations { get; set; }

    public int Messages { get; set; }

    public int Opportunities { get; set; }

    /// <summary>Raw webhook deliveries already processed that carried the number (they held its messages too).</summary>
    public int WebhookEvents { get; set; }
}
