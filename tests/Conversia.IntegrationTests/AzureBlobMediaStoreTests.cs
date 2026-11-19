using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Conversia.Application.Media;
using Conversia.Infrastructure.Media;
using Conversia.Tests.Shared;
using Testcontainers.Azurite;

namespace Conversia.IntegrationTests;

/// <summary>Azurite in a container: one emulator for the class.</summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    private readonly AzuriteContainer _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.35.0").WithInMemoryPersistence().Build();

    public BlobServiceClient Service { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Service = new BlobServiceClient(_container.GetConnectionString());
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>The production media store against Azurite (docs/MESSAGING.md, "Media storage"): the same contract as the local store.</summary>
public sealed class AzureBlobMediaStoreTests(AzuriteFixture azurite) : MediaStoreContract, IClassFixture<AzuriteFixture>
{
    private readonly AzureBlobMediaStore _store = new(azurite.Service.GetBlobContainerClient("media-" + Guid.NewGuid().ToString("N")[..12]));

    protected override IMediaStore Store => _store;

    [Fact]
    public async Task TheContainerItCreates_IsPrivate_AndTheBlobKeepsItsType()
    {
        var name = "media-" + Guid.NewGuid().ToString("N")[..12];
        var container = azurite.Service.GetBlobContainerClient(name);
        var tenant = Guid.NewGuid();

        var stored = await new AzureBlobMediaStore(container).PutAsync(tenant, new MemoryStream([1, 2, 3]), "application/pdf");

        Assert.Equal(PublicAccessType.None, (await container.GetPropertiesAsync()).Value.PublicAccess);
        Assert.Equal("application/pdf", (await container.GetBlobClient(stored.ObjectKey).GetPropertiesAsync()).Value.ContentType);
    }

    [Fact]
    public async Task AContainerThatAllowsPublicReads_IsRefused()
    {
        var container = azurite.Service.GetBlobContainerClient("public-" + Guid.NewGuid().ToString("N")[..12]);
        await container.CreateAsync(PublicAccessType.Blob);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AzureBlobMediaStore(container).PutAsync(Guid.NewGuid(), new MemoryStream([1]), "image/png"));
        Assert.Empty(container.GetBlobs().ToList());
    }
}
