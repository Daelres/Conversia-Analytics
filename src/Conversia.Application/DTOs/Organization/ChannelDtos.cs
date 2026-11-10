using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Organization;

/// <summary>Body of <c>POST /api/channels</c>. Carries no tenant: the channel always lands in the caller's tenant.</summary>
public class CreateChannelRequest
{
    [Display(Name = "Sucursal")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    public Guid? BranchId { get; set; }

    [Display(Name = "Número de teléfono")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [DisplayPhoneNumber]
    public string DisplayPhoneNumber { get; set; } = string.Empty;

    /// <summary>The provider's id of the number (<c>phone_number_id</c>), when known. Optional.</summary>
    [Display(Name = "Identificador del número en el proveedor")]
    [ProviderPhoneNumberId]
    public string? ProviderPhoneNumberId { get; set; }
}

/// <summary>
/// Body of <c>PUT /api/channels/{id}/provider-credentials</c>. <see cref="ApiKey"/> is write-only: it is encrypted before it is stored
/// and no response ever carries it. Leave it out (or blank) to keep the stored key and change only the environment.
/// </summary>
public class SetProviderCredentialsRequest
{
    [Display(Name = "Clave de API del proveedor")]
    [ProviderApiKey]
    public string? ApiKey { get; set; }

    /// <summary>A <see cref="WhatsAppProviderEnvironment"/> name: <c>Sandbox</c> or <c>Production</c>. It selects a configured base URL; no URL is ever taken from a request.</summary>
    [Display(Name = "Entorno del proveedor")]
    [Required(ErrorMessage = ValidationMessages.Required)]
    [EnumName(typeof(WhatsAppProviderEnvironment))]
    public string? Environment { get; set; }

    public override string ToString() => $"SetProviderCredentialsRequest {{ ApiKeyGiven = {!string.IsNullOrWhiteSpace(ApiKey)}, Environment = {Environment} }}";
}

/// <summary>A channel as listed. It never carries the webhook secret nor its hash, nor the provider API key in any form.</summary>
public class ChannelResponse
{
    public Guid Id { get; set; }

    public Guid BranchId { get; set; }

    public string DisplayPhoneNumber { get; set; } = string.Empty;

    public string? ProviderPhoneNumberId { get; set; }

    /// <summary>The public key of the webhook URL. Only callers who can manage the organization see it; for the others it is absent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelKey { get; set; }

    /// <summary>The relative URL the provider must call, for example <c>/api/webhooks/360dialog/{channelKey}</c>. Absent with the key.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebhookPath { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Whether a provider API key is stored for the channel. The key itself is never returned.</summary>
    public bool HasProviderApiKey { get; set; }

    /// <summary>When the stored key was set; null while there is none.</summary>
    public DateTime? ProviderApiKeySetAt { get; set; }

    /// <summary><c>Sandbox</c> or <c>Production</c>: which installation of the provider the channel sends through.</summary>
    public string ProviderEnvironment { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>The same channel without the key that identifies its webhook URL, for callers who may look but not manage.</summary>
    public ChannelResponse WithoutWebhookAddress() => new()
    {
        Id = Id,
        BranchId = BranchId,
        DisplayPhoneNumber = DisplayPhoneNumber,
        ProviderPhoneNumberId = ProviderPhoneNumberId,
        IsActive = IsActive,
        HasProviderApiKey = HasProviderApiKey,
        ProviderApiKeySetAt = ProviderApiKeySetAt,
        ProviderEnvironment = ProviderEnvironment,
        CreatedAt = CreatedAt,
    };

    public static ChannelResponse From(WhatsAppChannel channel) => new()
    {
        Id = channel.Id,
        BranchId = channel.BranchId,
        DisplayPhoneNumber = channel.DisplayPhoneNumber,
        ProviderPhoneNumberId = channel.ProviderPhoneNumberId,
        ChannelKey = channel.ChannelKey,
        WebhookPath = WebhookRoutes.Dialog360Path(channel.ChannelKey),
        IsActive = channel.IsActive,
        HasProviderApiKey = channel.HasProviderApiKey,
        ProviderApiKeySetAt = channel.ProviderApiKeySetAt,
        ProviderEnvironment = channel.ProviderEnvironment.ToString(),
        CreatedAt = channel.CreatedAt,
    };
}

/// <summary>The answer to creating a channel, or to rotating its secret: the only time a webhook secret is ever shown.</summary>
public class CreateChannelResponse : ChannelResponse
{
    /// <summary>Plaintext secret for the <c>X-Webhook-Secret</c> header (or the password of <c>Authorization: Basic</c>). Store it now: only its hash is kept.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public static CreateChannelResponse From(WhatsAppChannel channel, string webhookSecret) => new()
    {
        Id = channel.Id,
        BranchId = channel.BranchId,
        DisplayPhoneNumber = channel.DisplayPhoneNumber,
        ProviderPhoneNumberId = channel.ProviderPhoneNumberId,
        ChannelKey = channel.ChannelKey,
        WebhookPath = WebhookRoutes.Dialog360Path(channel.ChannelKey),
        IsActive = channel.IsActive,
        HasProviderApiKey = channel.HasProviderApiKey,
        ProviderApiKeySetAt = channel.ProviderApiKeySetAt,
        ProviderEnvironment = channel.ProviderEnvironment.ToString(),
        CreatedAt = channel.CreatedAt,
        WebhookSecret = webhookSecret,
    };
}
