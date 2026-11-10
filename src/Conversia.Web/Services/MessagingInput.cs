using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Web.Services;

/// <summary>The composer as posted. The rule of the text is the message's own, so the page and the API say the same thing.</summary>
public sealed class SendMessageInput
{
    public const int MaxLength = Message.TextMaxLength;

    private string? _text;

    /// <summary>
    /// A browser posts each line break as two characters while the text area's <c>maxlength</c> and the counter count it as one: the
    /// text is kept the way the person saw it counted, which is also how WhatsApp carries a line break.
    /// </summary>
    [Display(Name = "Mensaje")]
    [TrimmedText(MaxLength)]
    public string? Text
    {
        get => _text;
        set => _text = value?.Replace("\r\n", "\n");
    }

    /// <summary>Generated when the form is drawn and sent back with it: a form that is sent twice (double click, reload) sends one message.</summary>
    public string? SubmissionId { get; set; }

    public int Length => _text?.Length ?? 0;

    public SendMessageRequest ToRequest() => new() { Text = _text?.Trim() };
}

/// <summary>
/// The provider credentials form as posted. The key is write-only: it goes to the API and nowhere else, so this model is never
/// rendered back into a page and prints no key.
/// </summary>
public sealed class ProviderCredentialsInput
{
    [Display(Name = "Clave de API del proveedor")]
    [ProviderApiKey]
    public string? ApiKey { get; set; }

    [Display(Name = "Entorno del proveedor")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [EnumName(typeof(WhatsAppProviderEnvironment))]
    public string? Environment { get; set; }

    public bool KeyGiven => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>A blank key is sent as none: the API then keeps the stored one and changes only the environment.</summary>
    public SetProviderCredentialsRequest ToRequest() => new() { ApiKey = KeyGiven ? ApiKey!.Trim() : null, Environment = Environment };

    public override string ToString() => $"ProviderCredentialsInput {{ KeyGiven = {KeyGiven}, Environment = {Environment} }}";
}
