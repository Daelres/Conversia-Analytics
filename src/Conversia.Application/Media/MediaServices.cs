using Conversia.Application.Exceptions;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Media;

/// <param name="Enabled">A media store is configured (<c>Media:Store</c> is not <c>None</c>).</param>
/// <param name="DefaultRetentionDays">The platform default of the media retention (<c>Media:RetentionDays</c>).</param>
public sealed record MediaSettings(bool Enabled, int DefaultRetentionDays);

/// <summary>A stored file ready to stream: its content, the type it is served with, and whether it is downloaded rather than shown.</summary>
/// <param name="FileName">A generic name (<c>documento.pdf</c>): never the customer's file name.</param>
public sealed record MediaDownload(MediaContent Content, string ContentType, bool AsAttachment, string FileName);

/// <summary>Serves the stored media of a message to a user of its company (docs/MESSAGING.md, "Serving media").</summary>
public interface IMessageMediaService
{
    /// <summary>
    /// The stored copy of the media of a message of the conversation; <see cref="MessageNotFoundException"/> for an unknown message, one of
    /// another conversation or company (the same 404), or one whose copy is not stored (pending, refused, purged).
    /// </summary>
    Task<MediaDownload> OpenAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default);
}

public sealed class MessageMediaService(IMessageRepository messages, IMediaStore store) : IMessageMediaService
{
    public async Task<MediaDownload> OpenAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        // The repository is tenant-filtered: another company's message is not found, like an unknown one.
        var message = await messages.GetInConversationAsync(conversationId, messageId, cancellationToken);
        if (message is not { MediaStatus: MediaStatus.Stored, MediaObjectKey: { } key, MediaContentType: { } contentType })
        {
            throw new MessageNotFoundException();
        }

        var content = await store.OpenReadAsync(message.TenantId, key, cancellationToken) ?? throw new MessageNotFoundException();
        var attachment = MediaPolicy.ServeAsAttachment(contentType);
        return new MediaDownload(content, contentType, attachment, FileNameOf(message.Type, contentType));
    }

    private static string FileNameOf(string type, string contentType) => (type, contentType) switch
    {
        (_, MediaPolicy.Pdf) => "documento.pdf",
        (_, MediaPolicy.PlainText) => "documento.txt",
        (_, MediaPolicy.Word) => "documento.doc",
        (_, MediaPolicy.WordOpenXml) => "documento.docx",
        (_, MediaPolicy.Excel) => "hoja.xls",
        (_, MediaPolicy.ExcelOpenXml) => "hoja.xlsx",
        (_, MediaPolicy.PowerPoint) => "presentacion.ppt",
        (_, MediaPolicy.PowerPointOpenXml) => "presentacion.pptx",
        ("image" or "sticker", _) => "imagen",
        ("audio", _) => "audio",
        ("video", _) => "video",
        _ => "archivo",
    };
}

/// <summary>The store when <c>Media:Store</c> is <c>None</c>: nothing can be stored or read; deleting is a no-op.</summary>
public sealed class UnavailableMediaStore : IMediaStore
{
    public Task<Domain.Entities.StoredMediaObject> PutAsync(Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken = default)
        => throw new MessagingException(MessagingProblem.MediaStorageUnavailable);

    public Task<MediaContent?> OpenReadAsync(Guid tenantId, string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<MediaContent?>(null);

    public Task DeleteAsync(Guid tenantId, string objectKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<int> DeleteAllAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(0);

    public IAsyncEnumerable<StoredObjectInfo> ListAsync(Guid tenantId, CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<StoredObjectInfo>();
}
