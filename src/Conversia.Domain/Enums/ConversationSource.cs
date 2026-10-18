namespace Conversia.Domain.Enums;

/// <summary>Where a conversation came from. Stored by name.</summary>
public enum ConversationSource
{
    /// <summary>Not determined (reserved for conversations imported or classified later).</summary>
    Unknown = 0,

    /// <summary>The customer wrote on their own.</summary>
    Organic = 1,

    /// <summary>The first message carried a click-to-WhatsApp ad referral.</summary>
    Ads = 2,
}
