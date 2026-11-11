using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;

namespace Conversia.Application.DTOs.Sandbox;

/// <summary>
/// Body of <c>POST /api/sandbox/conversations</c>: a simulated customer writes for the first time to one of the company's active
/// numbers (docs/SANDBOX.md). The customer's number is made up by the API; a request cannot name one.
/// </summary>
public class StartSandboxConversationRequest
{
    public static readonly string CustomerNameRuleMessage = $"El nombre del cliente {TextRules.RuleText(Contact.DisplayNameMaxLength)}.";

    public static readonly string TextRuleMessage = $"El texto del mensaje {TextRules.RuleText(Message.TextMaxLength)}.";

    [Display(Name = "Número de WhatsApp")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public Guid? ChannelId { get; set; }

    [Display(Name = "Nombre del cliente")]
    [TrimmedText(Contact.DisplayNameMaxLength)]
    public string? CustomerName { get; set; }

    [Display(Name = "Texto")]
    [TrimmedText(Message.TextMaxLength)]
    public string? Text { get; set; }
}

/// <summary>Body of <c>POST /api/sandbox/conversations/{id}/customer-messages</c>: the simulated customer of a sandbox conversation writes again.</summary>
public class SandboxCustomerMessageRequest
{
    [Display(Name = "Texto")]
    [TrimmedText(Message.TextMaxLength)]
    public string? Text { get; set; }
}

/// <summary>
/// The simulated message was put in the ingestion queue, like a delivery of the provider. It is not in a conversation yet: the
/// background worker stores it a moment later, so the answer carries no conversation or message id.
/// </summary>
public class SandboxMessageAcceptedResponse
{
    /// <summary>The simulated customer's number: made up, in a range no real subscriber can have.</summary>
    public string CustomerWaId { get; set; } = string.Empty;

    public DateTime SentAt { get; set; }
}

/// <summary>How many rows of each kind <c>DELETE /api/sandbox/data</c> removed.</summary>
public class SandboxDataDeletedResponse
{
    public int Conversations { get; set; }

    public int Messages { get; set; }

    public int Contacts { get; set; }

    public int WebhookEvents { get; set; }
}
