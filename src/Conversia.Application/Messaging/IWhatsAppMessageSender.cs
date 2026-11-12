using Conversia.Domain.Entities;

namespace Conversia.Application.Messaging;

/// <summary>
/// Sends a WhatsApp message through one way of reaching the customer (a provider's API today). It reports every outcome of the provider
/// as a <see cref="WhatsAppSendResult"/> and never throws for one; no provider type crosses this port.
/// </summary>
public interface IWhatsAppMessageSender
{
    Task<WhatsAppSendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Chooses the sender of a channel. The choice is made per channel, per call, so different channels (or tenants) can send through
/// different adapters; nothing may hold on to one sender for all of them.
/// </summary>
public interface IWhatsAppMessageSenderResolver
{
    /// <param name="channel">The line the message leaves from.</param>
    /// <param name="sandboxMode">
    /// Whether the channel's company is in sandbox mode (docs/SANDBOX.md). While it is, the sender is the internal one: it reaches no
    /// provider and needs no credentials.
    /// </param>
    IWhatsAppMessageSender Resolve(WhatsAppChannel channel, bool sandboxMode);
}

/// <summary>A plain-text message for one customer of a channel. A class with its own <c>ToString</c>: a record would print the number and the text.</summary>
public sealed class WhatsAppTextMessage(WhatsAppChannel channel, string recipientWaId, string text)
{
    /// <summary>The line the message leaves from; it carries the (encrypted) credentials and the provider environment.</summary>
    public WhatsAppChannel Channel { get; } = channel;

    /// <summary>The customer's WhatsApp id: digits only, as stored on the contact.</summary>
    public string RecipientWaId { get; } = recipientWaId;

    public string Text { get; } = text;

    public override string ToString() => $"WhatsAppTextMessage {{ ChannelId = {Channel.Id}, TextLength = {Text.Length} }}";
}

public enum WhatsAppSendStatus
{
    /// <summary>The provider accepted the message.</summary>
    Sent,

    /// <summary>The provider does not recognize the channel's credentials.</summary>
    InvalidCredentials,

    /// <summary>The provider does not let this channel write to this recipient (in a sandbox, anyone but the tester's own number).</summary>
    RecipientNotAllowed,

    /// <summary>The provider's quota for the credentials is used up.</summary>
    LimitReached,

    /// <summary>The provider refused the message for another reason; sending the same message again will not help.</summary>
    Rejected,

    /// <summary>
    /// The provider could not be reached, timed out or failed. Whether the message left is unknown after a timeout, so nothing is
    /// retried automatically.
    /// </summary>
    TransientFailure,
}

/// <param name="ProviderMessageId">The provider's id of a sent message; null when it was not sent, or the provider's answer carried no usable id.</param>
public sealed record WhatsAppSendResult(WhatsAppSendStatus Status, string? ProviderMessageId)
{
    public static WhatsAppSendResult Sent(string? providerMessageId) => new(WhatsAppSendStatus.Sent, providerMessageId);

    public static WhatsAppSendResult Failed(WhatsAppSendStatus status)
        => status == WhatsAppSendStatus.Sent ? throw new ArgumentException("A failure cannot be Sent.", nameof(status)) : new(status, null);
}

/// <summary>
/// What a stored provider key is bound to. The protector authenticates this text together with the key, so a ciphertext copied to
/// another channel's row cannot be opened there.
/// </summary>
public static class ProviderCredentialPurpose
{
    public static string ForChannel(Guid channelId) => $"whatsapp-channel-api-key:{channelId:D}";
}
