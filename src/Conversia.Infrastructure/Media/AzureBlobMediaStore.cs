using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Conversia.Application.Media;
using Conversia.Domain.Entities;

namespace Conversia.Infrastructure.Media;

/// <summary>
/// The production media store (docs/MESSAGING.md, "Media storage"): Azure Blob Storage, one private container, one blob per object named by
/// its key (so each company has its own prefix). Azure encrypts every blob at rest (server-side encryption, always on); the container is created
/// private and the store refuses to write into one that allows public access. Blobs are never handed out by URL: the API streams them.
/// </summary>
public sealed class AzureBlobMediaStore : IMediaStore
{
    private readonly BlobContainerClient _container;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _ensure = new(1, 1);
    private bool _ready;

    public AzureBlobMediaStore(BlobContainerClient container, TimeProvider? time = null)
    {
        _container = container;
        _time = time ?? TimeProvider.System;
    }

    public async Task<StoredMediaObject> PutAsync(Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        await EnsureContainerAsync(cancellationToken);
        // Streamed from where it is (a temporary file of the download job, the uploaded form file): never copied whole into memory.
        Stream source = content;
        FileStream? spool = null;
        if (!content.CanSeek)
        {
            spool = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            await content.CopyToAsync(spool, cancellationToken);
            spool.Position = 0;
            source = spool;
        }

        await using var _ = spool;
        var length = source.Length - source.Position;
        if (length <= 0)
        {
            throw new ArgumentException("An empty media object cannot be stored.", nameof(content));
        }

        var objectKey = MediaObjectKeys.New(tenantId, _time.GetUtcNow().UtcDateTime);
        await _container.GetBlobClient(objectKey).UploadAsync(source, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }, // a key is never overwritten
        }, cancellationToken);
        return new StoredMediaObject(objectKey, length, contentType);
    }

    public async Task<MediaContent?> OpenReadAsync(Guid tenantId, string objectKey, CancellationToken cancellationToken = default)
    {
        MediaObjectKeys.Require(tenantId, objectKey);
        var blob = _container.GetBlobClient(objectKey);
        try
        {
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            var stream = await blob.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), cancellationToken);
            return new MediaContent(stream, properties.Value.ContentLength);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(Guid tenantId, string objectKey, CancellationToken cancellationToken = default)
    {
        MediaObjectKeys.Require(tenantId, objectKey);
        try
        {
            await _container.GetBlobClient(objectKey).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // The container itself does not exist: nothing to delete.
        }
    }

    public async Task<int> DeleteAllAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        try
        {
            await foreach (var item in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, MediaObjectKeys.Prefix(tenantId), cancellationToken))
            {
                if (await _container.GetBlobClient(item.Name).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken))
                {
                    deleted++;
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return deleted;
        }

        return deleted;
    }

    public async IAsyncEnumerable<StoredObjectInfo> ListAsync(Guid tenantId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pages = _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, MediaObjectKeys.Prefix(tenantId), cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            BlobItem item;
            try
            {
                if (!await pages.MoveNextAsync())
                {
                    break;
                }

                item = pages.Current;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                break;
            }

            if (MediaObjectKeys.BelongsTo(tenantId, item.Name))
            {
                yield return new StoredObjectInfo(item.Name, item.Properties.LastModified ?? item.Properties.CreatedOn ?? DateTimeOffset.UtcNow);
            }
        }

        await pages.DisposeAsync();
    }

    // Once per instance: the container exists and is private. A container that allows anonymous reads is a configuration error the store refuses
    // to write into (the objects are customers' files).
    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_ready)
        {
            return;
        }

        await _ensure.WaitAsync(cancellationToken);
        try
        {
            if (_ready)
            {
                return;
            }

            await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
            var properties = await _container.GetPropertiesAsync(cancellationToken: cancellationToken);
            if (properties.Value.PublicAccess is { } access && access != PublicAccessType.None)
            {
                throw new InvalidOperationException("The media container allows public access; media is not stored until it is made private.");
            }

            _ready = true;
        }
        finally
        {
            _ensure.Release();
        }
    }
}
