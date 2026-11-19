using System.Security.Cryptography;
using Conversia.API.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Conversia.IntegrationTests;

/// <summary>The media settings at startup (docs/DOCKER.md, "Media storage"): the local store is for development and single-instance installs only.</summary>
public class MediaConfigurationTests
{
    private static IConfiguration Settings(params (string Key, string Value)[] extra)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Media:Store"] = "Local",
            ["Media:Local:RootPath"] = Path.Combine(Path.GetTempPath(), "conversia-media-config"),
            ["Media:Local:EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        }.Concat(extra.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))).Build();

    private static IHostEnvironment Environment(string name) => new HostingEnvironment { EnvironmentName = name };

    [Fact]
    public void TheLocalStore_OutsideDevelopment_StopsTheApi_NamingTheSetting()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMedia(Settings(), Environment(Environments.Production)));

        Assert.Contains(MediaServiceCollectionExtensions.LocalAllowOutsideDevelopmentKey, error.Message);
    }

    [Fact]
    public void TheLocalStore_OutsideDevelopment_StartsWithTheExplicitYes()
        => new ServiceCollection().AddMedia(Settings((MediaServiceCollectionExtensions.LocalAllowOutsideDevelopmentKey, "true")), Environment(Environments.Production));

    [Fact]
    public void TheLocalStore_InDevelopment_NeedsNoYes()
        => new ServiceCollection().AddMedia(Settings(), Environment(Environments.Development));

    [Fact]
    public void ADownloadDeadlineOutOfRange_StopsTheApi()
        => Assert.ThrowsAny<Exception>(() => new ServiceCollection().AddMedia(Settings(("Media:DownloadTimeoutSeconds", "0")), Environment(Environments.Development)));
}
