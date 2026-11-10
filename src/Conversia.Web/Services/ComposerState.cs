using Conversia.Application.DTOs.Conversations;
using Conversia.Domain.Enums;

namespace Conversia.Web.Services;

public enum ComposerAvailability
{
    /// <summary>The page has not loaded the conversation (yet): nothing is offered and nothing is explained.</summary>
    Unknown,
    Available,
    NoPermission,
    ConversationClosed,
    ChannelInactive,
    CredentialsMissing,
}

/// <summary>
/// Whether the composer of a conversation is offered, and the reason when it is not. This only decides what the page shows: the API
/// checks every one of these again on each send.
/// </summary>
/// <param name="Sandbox">The line sends through the provider's sandbox, which has limits a person must know before writing.</param>
public sealed record ComposerState(ComposerAvailability Availability, bool Sandbox = false)
{
    public static readonly ComposerState Unknown = new(ComposerAvailability.Unknown);

    public bool CanSend => Availability == ComposerAvailability.Available;

    public string? Reason => Availability switch
    {
        ComposerAvailability.NoPermission => $"Tu cuenta no tiene el permiso «{PermissionLabels.For(Permission.ConversationsManage)}», que se necesita para enviar mensajes.",
        ComposerAvailability.ConversationClosed => "Esta conversación está cerrada y ya no admite mensajes.",
        ComposerAvailability.ChannelInactive => "El número de WhatsApp de esta conversación está desactivado. Actívalo en Organización para poder enviar mensajes.",
        ComposerAvailability.CredentialsMissing => "El número de WhatsApp de esta conversación no tiene clave de API del proveedor. Configúrala en Organización para poder enviar mensajes.",
        _ => null,
    };

    /// <param name="channel">What the conversation says about its line; null (an answer without it) hides nothing.</param>
    public static ComposerState For(bool canManage, string? conversationStatus, ConversationChannelState? channel)
    {
        if (!canManage)
        {
            return new(ComposerAvailability.NoPermission);
        }

        if (conversationStatus != nameof(ConversationStatus.Open))
        {
            return new(ComposerAvailability.ConversationClosed);
        }

        return channel switch
        {
            { IsActive: false } => new(ComposerAvailability.ChannelInactive),
            { HasProviderApiKey: false } => new(ComposerAvailability.CredentialsMissing),
            _ => new(ComposerAvailability.Available, Sandbox: channel?.ProviderEnvironment == MessagingLabels.Sandbox),
        };
    }
}
