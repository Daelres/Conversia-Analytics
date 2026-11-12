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

    /// <summary>A sandbox conversation while the company is not in sandbox mode: nothing real is ever sent into it.</summary>
    SandboxConversation,

    /// <summary>A real conversation while the company is in sandbox mode: nothing real is sent, and nothing made up is stored in it.</summary>
    SandboxModeActive,
}

/// <summary>
/// Whether the composer of a conversation is offered, and the reason when it is not. This only decides what the page shows: the API
/// checks every one of these again on each send.
/// </summary>
/// <param name="Sandbox">The line sends through the provider's sandbox, which has limits a person must know before writing.</param>
/// <param name="Internal">The company is in the internal sandbox mode (docs/SANDBOX.md): the message is recorded and reaches nobody.</param>
public sealed record ComposerState(ComposerAvailability Availability, bool Sandbox = false, bool Internal = false)
{
    public static readonly ComposerState Unknown = new(ComposerAvailability.Unknown);

    public bool CanSend => Availability == ComposerAvailability.Available;

    public string? Reason => Availability switch
    {
        ComposerAvailability.NoPermission => $"Tu cuenta no tiene el permiso «{PermissionLabels.For(Permission.ConversationsManage)}», que se necesita para enviar mensajes.",
        ComposerAvailability.ConversationClosed => "Esta conversación está cerrada y ya no admite mensajes.",
        ComposerAvailability.ChannelInactive => "El número de WhatsApp de esta conversación está desactivado. Actívalo en Organización para poder enviar mensajes.",
        ComposerAvailability.CredentialsMissing => "El número de WhatsApp de esta conversación no tiene clave de API del proveedor. Configúrala en Organización para poder enviar mensajes.",
        ComposerAvailability.SandboxConversation => SendFailure.Describe(SendFailure.SandboxConversation),
        ComposerAvailability.SandboxModeActive => SendFailure.Describe(SendFailure.SandboxModeActive),
        _ => null,
    };

    /// <param name="channel">What the conversation says about its line; null (an answer without it) hides nothing.</param>
    /// <param name="sandboxMode">The company is in the internal sandbox mode.</param>
    /// <param name="sandboxConversation">The conversation is sandbox data.</param>
    public static ComposerState For(
        bool canManage, string? conversationStatus, ConversationChannelState? channel, bool sandboxMode = false, bool sandboxConversation = false)
    {
        if (!canManage)
        {
            return new(ComposerAvailability.NoPermission);
        }

        if (conversationStatus != nameof(ConversationStatus.Open))
        {
            return new(ComposerAvailability.ConversationClosed);
        }

        if (sandboxConversation != sandboxMode)
        {
            return new(sandboxConversation ? ComposerAvailability.SandboxConversation : ComposerAvailability.SandboxModeActive);
        }

        if (sandboxMode)
        {
            // The internal sender reaches no provider: no key is needed and the limits of the provider's own sandbox do not apply.
            return channel is { IsActive: false } ? new(ComposerAvailability.ChannelInactive) : new(ComposerAvailability.Available, Internal: true);
        }

        return channel switch
        {
            { IsActive: false } => new(ComposerAvailability.ChannelInactive),
            { HasProviderApiKey: false } => new(ComposerAvailability.CredentialsMissing),
            _ => new(ComposerAvailability.Available, Sandbox: channel?.ProviderEnvironment == MessagingLabels.Sandbox),
        };
    }
}

/// <summary>
/// Whether "write as the customer" is offered on a conversation (docs/SANDBOX.md): only on an open sandbox conversation of an active
/// line while the company is in sandbox mode, to a person who may write. On anything else the control is not drawn at all (it is not
/// part of a real conversation's page), and the API refuses a forged post.
/// </summary>
public static class CustomerComposer
{
    public static ComposerState For(bool canManage, string? conversationStatus, ConversationChannelState? channel, bool sandboxMode, bool sandboxConversation)
        => canManage && sandboxMode && sandboxConversation && conversationStatus == nameof(ConversationStatus.Open) && channel is not { IsActive: false }
            ? new ComposerState(ComposerAvailability.Available, Internal: true)
            : ComposerState.Unknown;
}

