using System.Security.Cryptography;
using System.Text;
using Conversia.Application.Media;
using Conversia.Domain.Entities;

namespace Conversia.Infrastructure.Media;

/// <summary>
/// The media store for development and tests (docs/MESSAGING.md, "Media storage"): one file per object under <c>{root}/{key}.bin</c>,
/// encrypted with AES-256-GCM. The file is <c>"CVM1" || nonce (12) || tag (16) || ciphertext</c>, with a random nonce per object and the object
/// key as associated data, so a file moved to another key does not open. A read decrypts the whole object into memory (seekable, for range
/// requests): fine for development volumes, which is why production uses <see cref="AzureBlobMediaStore"/>.
/// </summary>
public sealed class LocalEncryptedMediaStore : IMediaStore
{
    private static readonly byte[] Magic = "CVM1"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _root;
    private readonly byte[] _key;
    private readonly TimeProvider _time;

    public LocalEncryptedMediaStore(string rootPath, byte[] key, TimeProvider? time = null)
    {
        if (key is not { Length: 32 })
        {
            throw new ArgumentException("The media encryption key must be 32 bytes.", nameof(key));
        }

        _root = Path.GetFullPath(rootPath);
        _key = key.ToArray();
        _time = time ?? TimeProvider.System;
    }

    public async Task<StoredMediaObject> PutAsync(Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
        {
            throw new ArgumentException("An empty media object cannot be stored.", nameof(content));
        }

        var objectKey = MediaObjectKeys.New(tenantId, _time.GetUtcNow().UtcDateTime);
        var plaintext = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(objectKey));
        }

        var path = PathOf(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await file.WriteAsync(Magic, cancellationToken);
            await file.WriteAsync(nonce, cancellationToken);
            await file.WriteAsync(tag, cancellationToken);
            await file.WriteAsync(ciphertext, cancellationToken);
        }

        return new StoredMediaObject(objectKey, buffer.Length, contentType);
    }

    public async Task<MediaContent?> OpenReadAsync(Guid tenantId, string objectKey, CancellationToken cancellationToken = default)
    {
        MediaObjectKeys.Require(tenantId, objectKey);
        var path = PathOf(objectKey);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] file;
        try
        {
            file = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        var header = Magic.Length + NonceSize + TagSize;
        if (file.Length < header || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("The stored media object is not in the expected format.");
        }

        var plaintext = new byte[file.Length - header];
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Decrypt(
                file.AsSpan(Magic.Length, NonceSize), file.AsSpan(header), file.AsSpan(Magic.Length + NonceSize, TagSize), plaintext, AssociatedData(objectKey));
        }

        return new MediaContent(new MemoryStream(plaintext, writable: false), plaintext.Length);
    }

    public Task DeleteAsync(Guid tenantId, string objectKey, CancellationToken cancellationToken = default)
    {
        MediaObjectKeys.Require(tenantId, objectKey);
        try
        {
            File.Delete(PathOf(objectKey)); // no error when the file does not exist
        }
        catch (DirectoryNotFoundException)
        {
            // Nor when its folder does not: never stored.
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteAllAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_root, tenantId.ToString("N"));
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(0);
        }

        var count = Directory.GetFiles(directory, "*.bin", SearchOption.AllDirectories).Length;
        Directory.Delete(directory, recursive: true);
        return Task.FromResult(count);
    }

    public async IAsyncEnumerable<StoredObjectInfo> ListAsync(Guid tenantId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_root, tenantId.ToString("N"));
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.bin", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            var key = relative[..^".bin".Length];
            if (MediaObjectKeys.BelongsTo(tenantId, key))
            {
                yield return new StoredObjectInfo(key, File.GetLastWriteTimeUtc(file));
            }
        }

        await Task.CompletedTask;
    }

    private static byte[] AssociatedData(string objectKey) => Encoding.UTF8.GetBytes("conversia-media:" + objectKey);

    // The key is validated (lower-case letters, digits, '-' and '/', no empty segment), so it can never leave the root.
    private string PathOf(string objectKey)
    {
        var path = Path.GetFullPath(Path.Combine(_root, objectKey.Replace('/', Path.DirectorySeparatorChar) + ".bin"));
        return path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : throw new ArgumentException("The media object key leaves the store's root.", nameof(objectKey));
    }
}
