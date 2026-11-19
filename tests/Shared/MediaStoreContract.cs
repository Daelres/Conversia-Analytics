using Conversia.Application.Media;

namespace Conversia.Tests.Shared;

/// <summary>
/// The behaviour every <see cref="IMediaStore"/> adapter must have (docs/MESSAGING.md, "Media"): the local encrypted store runs it in the
/// unit tests, the Azure Blob store against Azurite in the integration tests. Synthetic bytes only.
/// </summary>
public abstract class MediaStoreContract
{
    protected static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    protected abstract IMediaStore Store { get; }

    private static MemoryStream Bytes(int length, byte seed = 7) => new(Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed)).ToArray());

    private static async Task<byte[]> ReadAllAsync(MediaContent content)
    {
        using var buffer = new MemoryStream();
        await content.Content.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task AStoredObject_ReadsBackTheSameBytes_UnderARandomKeyOfItsCompany()
    {
        var tenant = Guid.NewGuid();
        var original = Bytes(70_000);

        var stored = await Store.PutAsync(tenant, original, "image/jpeg");

        Assert.True(MediaObjectKeys.BelongsTo(tenant, stored.ObjectKey));
        Assert.Equal(70_000, stored.SizeBytes);
        Assert.Equal("image/jpeg", stored.ContentType);
        await using var content = await Store.OpenReadAsync(tenant, stored.ObjectKey);
        Assert.NotNull(content);
        Assert.Equal(70_000, content.Length);
        Assert.Equal(original.ToArray(), await ReadAllAsync(content));
    }

    [Fact]
    public async Task TwoObjectsWithTheSameBytes_GetDifferentKeys()
    {
        var tenant = Guid.NewGuid();

        var first = await Store.PutAsync(tenant, Bytes(10), "application/pdf");
        var second = await Store.PutAsync(tenant, Bytes(10), "application/pdf");

        Assert.NotEqual(first.ObjectKey, second.ObjectKey);
    }

    [Fact]
    public async Task TheStreamIsSeekable_ForRangeRequests()
    {
        var tenant = Guid.NewGuid();
        var stored = await Store.PutAsync(tenant, Bytes(1000), "audio/ogg");

        await using var content = await Store.OpenReadAsync(tenant, stored.ObjectKey);
        Assert.True(content!.Content.CanSeek);
        content.Content.Seek(500, SeekOrigin.Begin);
        var buffer = new byte[10];
        await content.Content.ReadExactlyAsync(buffer);
        Assert.Equal(Bytes(1000).ToArray()[500..510], buffer);
    }

    [Fact]
    public async Task AMissingObject_ReadsAsNull()
        => Assert.Null(await Store.OpenReadAsync(Guid.NewGuid() is var tenant ? tenant : default, MediaObjectKeys.New(tenant, Now)));

    [Fact]
    public async Task AnotherCompanysKey_IsRefused_ForEveryOperation()
    {
        var owner = Guid.NewGuid();
        var stored = await Store.PutAsync(owner, Bytes(10), "image/png");
        var other = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => Store.OpenReadAsync(other, stored.ObjectKey));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.DeleteAsync(other, stored.ObjectKey));
        Assert.NotNull(await Store.OpenReadAsync(owner, stored.ObjectKey));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("")]
    public async Task AMalformedKey_IsRefused(string key)
        => await Assert.ThrowsAsync<ArgumentException>(() => Store.OpenReadAsync(Guid.NewGuid(), key));

    [Fact]
    public async Task Deleting_IsIdempotent()
    {
        var tenant = Guid.NewGuid();
        var stored = await Store.PutAsync(tenant, Bytes(10), "image/png");

        await Store.DeleteAsync(tenant, stored.ObjectKey);
        await Store.DeleteAsync(tenant, stored.ObjectKey);

        Assert.Null(await Store.OpenReadAsync(tenant, stored.ObjectKey));
    }

    [Fact]
    public async Task DeletingACompany_RemovesAllItsObjects_AndNoOtherCompanys()
    {
        var tenant = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        var a = await Store.PutAsync(tenant, Bytes(10), "image/png");
        var b = await Store.PutAsync(tenant, Bytes(20), "application/pdf");
        var kept = await Store.PutAsync(neighbour, Bytes(30), "image/png");

        Assert.Equal(2, await Store.DeleteAllAsync(tenant));
        Assert.Equal(0, await Store.DeleteAllAsync(tenant));

        Assert.Null(await Store.OpenReadAsync(tenant, a.ObjectKey));
        Assert.Null(await Store.OpenReadAsync(tenant, b.ObjectKey));
        Assert.NotNull(await Store.OpenReadAsync(neighbour, kept.ObjectKey));
    }

    [Fact]
    public async Task AnEmptyObject_IsRefused()
        => await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.PutAsync(Guid.NewGuid(), new MemoryStream(), "image/png"));

    [Fact]
    public async Task ListingACompany_GivesItsKeysAndWhenTheyWereWritten_AndNoOtherCompanys()
    {
        var tenant = Guid.NewGuid();
        var a = await Store.PutAsync(tenant, Bytes(10), "image/png");
        var b = await Store.PutAsync(tenant, Bytes(20), "application/pdf");
        await Store.PutAsync(Guid.NewGuid(), Bytes(30), "image/png");

        var listed = new List<StoredObjectInfo>();
        await foreach (var item in Store.ListAsync(tenant))
        {
            listed.Add(item);
        }

        Assert.Equal(new[] { a.ObjectKey, b.ObjectKey }.Order(), listed.Select(i => i.ObjectKey).Order());
        Assert.All(listed, i => Assert.True(i.LastModified > DateTimeOffset.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public async Task AStreamIsStoredFromWhereItIs_WithoutBeingCopiedWhole()
    {
        var tenant = Guid.NewGuid();
        var source = Bytes(5000);
        source.Position = 0;

        var stored = await Store.PutAsync(tenant, source, "image/png");

        Assert.Equal(5000, stored.SizeBytes);
    }

    [Fact]
    public async Task DeletingAKeyThatWasNeverStored_IsNotAnError()
    {
        var tenant = Guid.NewGuid();

        await Store.DeleteAsync(tenant, MediaObjectKeys.New(tenant, Now));
    }
}
