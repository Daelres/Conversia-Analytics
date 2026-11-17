using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;

namespace Conversia.Application.DTOs.Conversations;

/// <summary>What happened to a read receipt or a typing indicator (docs/MESSAGING.md, "Read receipts and typing"). Never an error for the advisor.</summary>
public static class PresenceOutcome
{
    /// <summary>The company has read receipts off: nothing was sent.</summary>
    public const string Disabled = "disabled";

    /// <summary>Nothing to do: no unread customer message, or no reply is plausible (the customer did not write last, the window is closed).</summary>
    public const string NotNeeded = "not_needed";

    /// <summary>A typing indicator was sent less than 20 seconds ago for this conversation.</summary>
    public const string Throttled = "throttled";

    /// <summary>The provider accepted it.</summary>
    public const string Sent = "sent";

    /// <summary>Sandbox mode: the internal sender accepted it and nothing reached a phone.</summary>
    public const string Sandbox = "sandbox";

    /// <summary>It could not be sent (no key, closed conversation, the provider refused or failed). Logged; the advisor is not blocked.</summary>
    public const string Failed = "failed";
}

public class PresenceResponse
{
    public string Outcome { get; set; } = string.Empty;
}

/// <summary>The company's messaging settings (<c>GET</c>/<c>PUT /api/tenant/messaging-settings</c>).</summary>
public class MessagingSettingsResponse
{
    /// <summary>Opening a conversation marks the customer's messages as read on WhatsApp, and typing shows "escribiendo...". Off by default.</summary>
    public bool ReadReceiptsEnabled { get; set; }
}

public class SetMessagingSettingsRequest
{
    [Display(Name = "Confirmaciones de lectura")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public bool? ReadReceiptsEnabled { get; set; }
}
