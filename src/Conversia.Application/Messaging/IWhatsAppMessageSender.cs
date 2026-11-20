using Conversia.Domain.Entities;

namespace Conversia.Application.Messaging;

/// <summary>
/// Sends a WhatsApp message through one way of reaching the customer (a provider's API today). It reports every outcome of the provider
/// as a <see cref="WhatsAppSendResult"/> and never throws for one; no provider type crosses this port.
/// </summary>
public interface IWhatsAppMessageSender
{
    Task<WhatsAppSendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken = default);

    /// <summary>Sends an approved template (the only kind of message WhatsApp delivers outside the customer service window).</summary>
    Task<WhatsAppSendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a customer's message (and the earlier ones) as read on WhatsApp and, with <see cref="WhatsAppReadReceipt.Typing"/>, shows the
    /// typing indicator for up to 25 seconds. The answer carries no message id.
    /// </summary>
    Task<WhatsAppSendStatus> MarkReadAsync(WhatsAppReadReceipt receipt, CancellationToken cancellationToken = default);
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

/// <summary>An approved template for one customer of a channel, with the values of its placeholders in the order the template lists them.</summary>
public sealed class WhatsAppTemplateMessage(
    WhatsAppChannel channel, string recipientWaId, string name, string language, IReadOnlyList<WhatsAppTemplateParameter> parameters)
{
    public WhatsAppChannel Channel { get; } = channel;

    public string RecipientWaId { get; } = recipientWaId;

    public string Name { get; } = name;

    /// <summary>Meta's language code of the template (<c>es</c>, <c>es_CO</c>).</summary>
    public string Language { get; } = language;

    public IReadOnlyList<WhatsAppTemplateParameter> Parameters { get; } = parameters;

    public override string ToString() => $"WhatsAppTemplateMessage {{ ChannelId = {Channel.Id}, Name = {Name}, Parameters = {Parameters.Count} }}";
}

/// <summary>One body value of a template. <see cref="Name"/> is set for a named placeholder (<c>{{first_name}}</c>), null for a positional one.</summary>
public sealed class WhatsAppTemplateParameter(string? name, string value)
{
    public string? Name { get; } = name;

    public string Value { get; } = value;

    public override string ToString() => $"WhatsAppTemplateParameter {{ Name = {Name}, Length = {Value.Length} }}";
}

/// <param name="ProviderMessageId">The provider's id of the customer's message to mark as read (<c>wamid.</c>...).</param>
/// <param name="Typing">Also show the typing indicator (it marks the message as read too).</param>
public sealed record WhatsAppReadReceipt(WhatsAppChannel Channel, string ProviderMessageId, bool Typing)
{
    public override string ToString() => $"WhatsAppReadReceipt {{ ChannelId = {Channel.Id}, Typing = {Typing} }}";
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
    /// The provider refused a free-form message because more than 24 hours passed since the customer last wrote (Meta's error 131047):
    /// only a template can be sent.
    /// </summary>
    WindowClosed,

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
