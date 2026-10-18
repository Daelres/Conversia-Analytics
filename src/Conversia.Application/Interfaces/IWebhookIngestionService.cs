namespace Conversia.Application.Interfaces;

/// <summary>The channel an authenticated webhook call belongs to.</summary>
public sealed record WebhookChannelAccess(Guid ChannelId, Guid TenantId);

public enum WebhookStoreResult
{
    /// <summary>A new event was stored.</summary>
    Stored,

    /// <summary>The channel already had an event with the same body: nothing was stored, and the caller still answers 200.</summary>
    Duplicate,

    /// <summary>The body is empty, not a JSON object, or cannot be stored: the caller answers 400.</summary>
    InvalidPayload,
}

/// <summary>
/// The request path of a provider webhook: authenticate the call, then store the delivery untouched. It does no parsing and no
/// business work; <see cref="IWebhookEventProcessor"/> does that later, in the background.
/// </summary>
public interface IWebhookIngestionService
{
    /// <summary>
    /// Returns the channel when <paramref name="channelKey"/> names an active channel and <paramref name="secret"/> is its webhook secret
    /// (compared by SHA-256 hash in constant time), otherwise null, the same answer for an unknown key, an inactive channel and a wrong
    /// secret. On success the scope's tenant is set to the channel's, so everything after runs tenant-filtered.
    /// </summary>
    Task<WebhookChannelAccess?> AuthenticateAsync(string channelKey, string? secret, CancellationToken cancellationToken = default);

    /// <summary>Stores the raw body as a pending event, idempotently by body hash.</summary>
    Task<WebhookStoreResult> StoreAsync(WebhookChannelAccess channel, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default);
}
