using Conversia.Domain.Entities;

namespace Conversia.Application.Messaging;

/// <summary>
/// The media side of a provider (docs/MESSAGING.md, "Media"): download a received file by its media id, upload a file to send, and send a media
/// message by the uploaded id. Like <see cref="IWhatsAppMessageSender"/>, it reports outcomes and never throws for one; no provider type, URL or
/// key crosses this port.
/// </summary>
public interface IWhatsAppMediaClient
{
    /// <summary>Downloads at most <paramref name="maxBytes"/> of the media; a bigger file is <see cref="MediaDownloadStatus.TooLarge"/> without being read further.</summary>
    /// <param name="declaredMimeType">The type the webhook declared: a hint only (the internal sandbox client makes up a file of that kind).</param>
    Task<MediaDownloadResult> DownloadAsync(
        WhatsAppChannel channel, string providerMediaId, long maxBytes, string? declaredMimeType = null, CancellationToken cancellationToken = default);

    /// <summary>Uploads a file to the provider's media store; the answer carries the media id to send it by.</summary>
    /// <summary>Uploads a file to the provider's media store from the stream's position; the answer carries the media id to send it by.</summary>
    Task<MediaUploadResult> UploadAsync(WhatsAppChannel channel, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Sends an uploaded image or document to the customer, with an optional caption.</summary>
    Task<WhatsAppSendResult> SendMediaAsync(WhatsAppMediaMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Chooses the media client of a channel (the internal sandbox one while the company is in sandbox mode, docs/SANDBOX.md).</summary>
public interface IWhatsAppMediaClientResolver
{
    IWhatsAppMediaClient Resolve(WhatsAppChannel channel, bool sandbox);
}

public enum MediaDownloadStatus
{
    Downloaded,

    /// <summary>Bigger than the limit: not stored.</summary>
    TooLarge,

    /// <summary>The provider does not know the media (expired after its 7 days, or deleted).</summary>
    NotFound,

    /// <summary>The channel has no key, or the provider does not accept it: may be fixed by the company, so it is retried.</summary>
    CredentialsUnavailable,

    /// <summary>Another refusal that will not change on a retry.</summary>
    Rejected,

    /// <summary>Unreachable, timed out, 5xx, 429: retried with backoff.</summary>
    TransientFailure,
}

/// <param name="Content">
/// The file, when <see cref="Status"/> is <see cref="MediaDownloadStatus.Downloaded"/>: a seekable stream at position 0 (a temporary file that
/// deletes itself, for the real provider). The caller disposes the result.
/// </param>
/// <param name="DeclaredMimeType">What the provider says the type is; only a hint (the content decides, <c>MediaPolicy</c>).</param>
public sealed record MediaDownloadResult(MediaDownloadStatus Status, Stream? Content = null, string? DeclaredMimeType = null) : IAsyncDisposable
{
    public MediaDownloadResult(MediaDownloadStatus status, byte[] content, string? declaredMimeType)
        : this(status, new MemoryStream(content, writable: false), declaredMimeType)
    {
    }

    public static MediaDownloadResult Of(MediaDownloadStatus status) => new(status);

    public ValueTask DisposeAsync() => Content?.DisposeAsync() ?? ValueTask.CompletedTask;

    public override string ToString() => $"MediaDownloadResult {{ Status = {Status}, Length = {(Content is { CanSeek: true } c ? c.Length : null)} }}";
}

/// <param name="ProviderMediaId">The provider's id of the uploaded file, when <see cref="Status"/> is <see cref="WhatsAppSendStatus.Sent"/>.</param>
public sealed record MediaUploadResult(WhatsAppSendStatus Status, string? ProviderMediaId);

/// <summary>An uploaded image or document for one customer. A class with its own <c>ToString</c>: no number, no caption in a log.</summary>
public sealed class WhatsAppMediaMessage(WhatsAppChannel channel, string recipientWaId, string type, string providerMediaId, string? caption)
{
    public WhatsAppChannel Channel { get; } = channel;

    public string RecipientWaId { get; } = recipientWaId;

    /// <summary><c>image</c> or <c>document</c>.</summary>
    public string Type { get; } = type;

    public string ProviderMediaId { get; } = providerMediaId;

    public string? Caption { get; } = caption;

    public override string ToString() => $"WhatsAppMediaMessage {{ ChannelId = {Channel.Id}, Type = {Type}, HasCaption = {Caption is not null} }}";
}
