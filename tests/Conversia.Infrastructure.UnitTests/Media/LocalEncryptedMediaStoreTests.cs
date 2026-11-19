using System.Security.Cryptography;
using Conversia.Application.Media;
using Conversia.Infrastructure.Media;
using Conversia.Tests.Shared;

namespace Conversia.Infrastructure.UnitTests.Media;

/// <summary>The development and test store: files on disk, each encrypted with AES-256-GCM (docs/MESSAGING.md, "Media storage").</summary>
public sealed class LocalEncryptedMediaStoreTests : MediaStoreContract, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "conversia-media-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly LocalEncryptedMediaStore _store;

    public LocalEncryptedMediaStoreTests() => _store = new LocalEncryptedMediaStore(_root, _key);

    protected override IMediaStore Store => _store;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task TheFileOnDisk_IsNotThePlaintext()
    {
        var tenant = Guid.NewGuid();
        var plaintext = "%PDF-1.7 cotizacion de depilacion laser"u8.ToArray();

        await _store.PutAsync(tenant, new MemoryStream(plaintext), "application/pdf");

        var file = Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        var onDisk = await File.ReadAllBytesAsync(file);
        Assert.Equal(-1, onDisk.AsSpan().IndexOf("cotizacion"u8));
    }

    [Fact]
    public async Task AFileMovedToAnotherKey_CannotBeRead()
    {
        var tenant = Guid.NewGuid();
        var first = await _store.PutAsync(tenant, new MemoryStream([1, 2, 3]), "image/png");
        var second = await _store.PutAsync(tenant, new MemoryStream([4, 5, 6]), "image/png");
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        File.Copy(files.Single(f => f.Contains(first.ObjectKey.Split('/')[^1])), files.Single(f => f.Contains(second.ObjectKey.Split('/')[^1])), overwrite: true);

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => _store.OpenReadAsync(tenant, second.ObjectKey));
    }

    [Fact]
    public async Task AnotherKey_CannotReadTheFiles()
    {
        var tenant = Guid.NewGuid();
        var stored = await _store.PutAsync(tenant, new MemoryStream([1, 2, 3]), "image/png");

        var stranger = new LocalEncryptedMediaStore(_root, RandomNumberGenerator.GetBytes(32));

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => stranger.OpenReadAsync(tenant, stored.ObjectKey));
    }

    [Fact]
    public void AKeyThatIsNot32Bytes_IsRefused()
        => Assert.Throws<ArgumentException>(() => new LocalEncryptedMediaStore(_root, new byte[16]));
}
