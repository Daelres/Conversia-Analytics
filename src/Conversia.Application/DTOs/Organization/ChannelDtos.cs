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

    /// <summary>
    /// The number was disconnected from the API by a coexistence notification (Meta's <c>account_update</c>, docs/WEBHOOKS.md "Coexistence"):
    /// usually the WhatsApp Business app was not opened for about 14 days. Content-free: an event and a reason word.
    /// </summary>
    public bool ProviderDisconnected { get; set; }

    public string? ProviderDisconnectionEvent { get; set; }

    public string? ProviderDisconnectionReason { get; set; }

    public DateTime? ProviderDisconnectedAt { get; set; }

    /// <summary>The shared chat history is imported (off by default; only the owner turns it on, confirming the legal basis).</summary>
    public bool HistoryImportEnabled { get; set; }

    /// <summary>The imported conversations are read by the AI too (otherwise only by the rules).</summary>
    public bool HistoryAiAnalysisEnabled { get; set; }

    /// <summary>The user who last confirmed the legal basis to import past conversations, and when.</summary>
    public Guid? HistoryImportConsentedBy { get; set; }

    public DateTime? HistoryImportConsentedAt { get; set; }

    private T WithCoexistence<T>(T target) where T : ChannelResponse
    {
        target.ProviderDisconnected = ProviderDisconnected;
        target.ProviderDisconnectionEvent = ProviderDisconnectionEvent;
        target.ProviderDisconnectionReason = ProviderDisconnectionReason;
        target.ProviderDisconnectedAt = ProviderDisconnectedAt;
        target.HistoryImportEnabled = HistoryImportEnabled;
        target.HistoryAiAnalysisEnabled = HistoryAiAnalysisEnabled;
        target.HistoryImportConsentedBy = HistoryImportConsentedBy;
        target.HistoryImportConsentedAt = HistoryImportConsentedAt;
        return target;
    }

    internal static T Coexistence<T>(T target, WhatsAppChannel channel) where T : ChannelResponse
    {
        target.ProviderDisconnected = channel.IsProviderDisconnected;
        target.ProviderDisconnectionEvent = channel.ProviderDisconnectionEvent;
        target.ProviderDisconnectionReason = channel.ProviderDisconnectionReason;
        target.ProviderDisconnectedAt = channel.ProviderDisconnectedAt;
        target.HistoryImportEnabled = channel.HistoryImportEnabled;
        target.HistoryAiAnalysisEnabled = channel.HistoryAiAnalysisEnabled;
        target.HistoryImportConsentedBy = channel.HistoryImportConsentedBy;
        target.HistoryImportConsentedAt = channel.HistoryImportConsentedAt;
        return target;
    }

    /// <summary>The same channel without the key that identifies its webhook URL, for callers who may look but not manage.</summary>
    public ChannelResponse WithoutWebhookAddress() => WithCoexistence(new ChannelResponse
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
    });

    public static ChannelResponse From(WhatsAppChannel channel) => Coexistence(new ChannelResponse
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
    }, channel);
}

/// <summary>The answer to creating a channel, or to rotating its secret: the only time a webhook secret is ever shown.</summary>
public class CreateChannelResponse : ChannelResponse
{
    /// <summary>Plaintext secret for the <c>X-Webhook-Secret</c> header (or the password of <c>Authorization: Basic</c>). Store it now: only its hash is kept.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public static CreateChannelResponse From(WhatsAppChannel channel, string webhookSecret) => Coexistence(new CreateChannelResponse
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
    }, channel);
}

/// <summary>
/// Body of <c>PUT /api/channels/{id}/history-import</c> (only the company owner): turns the import of the shared chat history on or off. Turning it
/// on requires <see cref="LegalBasisConfirmed"/>: the owner confirms the company has the legal basis (Ley 1581) to process the past conversations.
/// </summary>
public class SetHistoryImportRequest
{
    public const string LegalBasisRuleMessage = "Para importar el historial debes confirmar que la empresa tiene la base legal para tratar esas conversaciones.";

    [Required(ErrorMessage = ValidationMessages.Required)]
    public bool? Enabled { get; set; }

    public bool LegalBasisConfirmed { get; set; }

    /// <summary>Also read the imported conversations with the AI (it costs per conversation). Only with <see cref="Enabled"/>.</summary>
    public bool AnalyzeWithAi { get; set; }
}
