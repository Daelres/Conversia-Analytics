using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Sandbox;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;

namespace Conversia.Web.Services;

/// <summary>The form that starts a simulated conversation, as posted. The rules are the API's own, so the page and the API say the same thing.</summary>
public sealed class StartSandboxConversationInput
{
    private string? _text;

    [Display(Name = "Número de WhatsApp")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public string? ChannelId { get; set; }

    [Display(Name = "Nombre del cliente")]
    [TrimmedText(Contact.DisplayNameMaxLength)]
    public string? CustomerName { get; set; }

    /// <summary>Line breaks are kept the way the person saw them counted (see <see cref="SendMessageInput.Text"/>).</summary>
    [Display(Name = "Primer mensaje")]
    [TrimmedText(Message.TextMaxLength)]
    public string? Text
    {
        get => _text;
        set => _text = value?.Replace("\r\n", "\n");
    }

    /// <summary>Generated when the form is drawn and sent back with it: a form that is sent twice starts one conversation.</summary>
    public string? SubmissionId { get; set; }

    public bool HasChannel => Guid.TryParse(ChannelId, out _);

    public StartSandboxConversationRequest ToRequest() => new()
    {
        ChannelId = Guid.TryParse(ChannelId, out var id) ? id : null,
        CustomerName = CustomerName?.Trim(),
        Text = _text?.Trim(),
    };
}

/// <summary>The confirmation form of deleting the sandbox data: it carries only the guard against a double submission.</summary>
public sealed class SandboxDataInput
{
    public string? SubmissionId { get; set; }
}
