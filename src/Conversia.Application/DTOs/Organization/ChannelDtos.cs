using System.ComponentModel.DataAnnotations;
using Conversia.Application.Validation;
using Conversia.Domain.Entities;

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

/// <summary>A channel as listed. It never carries the webhook secret nor its hash.</summary>
public class ChannelResponse
{
    public Guid Id { get; set; }

    public Guid BranchId { get; set; }

    public string DisplayPhoneNumber { get; set; } = string.Empty;

    public string? ProviderPhoneNumberId { get; set; }

    public string ChannelKey { get; set; } = string.Empty;

    /// <summary>The relative URL the provider must call, for example <c>/api/webhooks/360dialog/{channelKey}</c>.</summary>
    public string WebhookPath { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public static ChannelResponse From(WhatsAppChannel channel) => new()
    {
        Id = channel.Id,
        BranchId = channel.BranchId,
        DisplayPhoneNumber = channel.DisplayPhoneNumber,
        ProviderPhoneNumberId = channel.ProviderPhoneNumberId,
        ChannelKey = channel.ChannelKey,
        WebhookPath = WebhookRoutes.Dialog360Path(channel.ChannelKey),
        IsActive = channel.IsActive,
        CreatedAt = channel.CreatedAt,
    };
}

/// <summary>The answer to creating a channel: the only time the webhook secret is ever shown.</summary>
public class CreateChannelResponse : ChannelResponse
{
    /// <summary>Plaintext secret for the <c>X-Webhook-Secret</c> header. Store it now: only its hash is kept.</summary>
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
        CreatedAt = channel.CreatedAt,
        WebhookSecret = webhookSecret,
    };
}
