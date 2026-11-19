using System.Text;
using Conversia.Application.Messaging;
using Conversia.Application.Sandbox;
using Conversia.Domain.Entities;

namespace Conversia.Infrastructure.Messaging;

/// <summary>
/// The media client of the internal sandbox (docs/SANDBOX.md): no network call. A "download" of a sandbox media id returns a synthetic file
/// (an 8x8 PNG for an image, a one-page PDF otherwise); an upload and a send are accepted at once with sandbox ids. Nothing real is ever read.
/// </summary>
public sealed class SandboxMediaClient : IWhatsAppMediaClient
{
    /// <summary>A synthetic 8x8 PNG.</summary>
    public static readonly byte[] SyntheticPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR4nGPQ7nqEFTEMLQkAlFFlwclbVV4AAAAASUVORK5CYII=");

    /// <summary>A synthetic one-page PDF without content of any customer.</summary>
    public static readonly byte[] SyntheticPdf = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
        "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n");

    public Task<MediaDownloadResult> DownloadAsync(
        WhatsAppChannel channel, string providerMediaId, long maxBytes, string? declaredMimeType = null, CancellationToken cancellationToken = default)
    {
        if (!providerMediaId.StartsWith(SandboxIds.MediaIdPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult(MediaDownloadResult.Of(MediaDownloadStatus.NotFound));
        }

        var content = declaredMimeType == "application/pdf" ? SyntheticPdf : SyntheticPng;
        return Task.FromResult(new MediaDownloadResult(MediaDownloadStatus.Downloaded, content.ToArray(), declaredMimeType));
    }

    public Task<MediaUploadResult> UploadAsync(WhatsAppChannel channel, Stream content, string contentType, CancellationToken cancellationToken = default)
        => Task.FromResult(new MediaUploadResult(WhatsAppSendStatus.Sent, SandboxIds.NewMediaId()));

    public Task<WhatsAppSendResult> SendMediaAsync(WhatsAppMediaMessage message, CancellationToken cancellationToken = default)
        => Task.FromResult(WhatsAppSendResult.Sent(SandboxIds.NewOutboundMessageId()));
}

/// <summary>Sandbox data (a sandbox conversation, or a company in sandbox mode) goes to <see cref="SandboxMediaClient"/>; the rest to 360dialog.</summary>
public sealed class WhatsAppMediaClientResolver(Dialog360MediaClient dialog360, SandboxMediaClient sandbox) : IWhatsAppMediaClientResolver
{
    public IWhatsAppMediaClient Resolve(WhatsAppChannel channel, bool isSandbox) => isSandbox ? sandbox : dialog360;
}
