using System.Security.Cryptography;
using Conversia.Domain.Entities;

namespace Conversia.Application.Media;

/// <summary>
/// Where the copies of WhatsApp media are kept (docs/MESSAGING.md, "Media"). Objects are named by <see cref="MediaObjectKeys"/>: random, under
/// the company's prefix, never the customer's file name or number. No adapter hands out a public URL: the API streams every object to an
/// authenticated user of its company. Every method refuses a key outside the given company's prefix.
/// </summary>
public interface IMediaStore
{
    /// <summary>Stores <paramref name="content"/> (already checked and bounded by the caller) under a new key of the company.</summary>
    Task<StoredMediaObject> PutAsync(Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Opens a stored object for reading; null when it does not exist (deleted, purged). The stream is seekable (range requests).</summary>
    Task<MediaContent?> OpenReadAsync(Guid tenantId, string objectKey, CancellationToken cancellationToken = default);

    /// <summary>Deletes one object. Idempotent: a missing object is not an error.</summary>
    Task DeleteAsync(Guid tenantId, string objectKey, CancellationToken cancellationToken = default);

    /// <summary>Deletes every object of the company (its whole prefix) and returns how many there were. Idempotent.</summary>
    Task<int> DeleteAllAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>The objects of the company with when they were written: what the orphan sweep compares with the database.</summary>
    IAsyncEnumerable<StoredObjectInfo> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>A stored object as the store lists it: its key and when it was written.</summary>
public sealed record StoredObjectInfo(string ObjectKey, DateTimeOffset LastModified);

/// <summary>An opened stored object. The caller disposes it.</summary>
public sealed class MediaContent(Stream content, long length) : IAsyncDisposable, IDisposable
{
    public Stream Content { get; } = content;

    public long Length { get; } = length;

    public ValueTask DisposeAsync() => Content.DisposeAsync();

    public void Dispose() => Content.Dispose();
}

/// <summary>The names of stored objects: <c>{tenant}/{yyyy}/{MM}/{32 random hex}</c>. Nothing in a key comes from the file or the customer.</summary>
public static class MediaObjectKeys
{
    public static string Prefix(Guid tenantId) => $"{tenantId:N}/";

    public static string New(Guid tenantId, DateTime now)
        => $"{Prefix(tenantId)}{now:yyyy}/{now:MM}/{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";

    /// <summary>A well-formed key under the company's prefix (a store never reads or deletes outside it).</summary>
    public static bool BelongsTo(Guid tenantId, string? objectKey)
        => StoredMediaObject.IsValidKey(objectKey) && objectKey!.StartsWith(Prefix(tenantId), StringComparison.Ordinal);

    public static void Require(Guid tenantId, string objectKey)
    {
        if (!BelongsTo(tenantId, objectKey))
        {
            throw new ArgumentException("The media object key does not belong to the company.", nameof(objectKey));
        }
    }
}
