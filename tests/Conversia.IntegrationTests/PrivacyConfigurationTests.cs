using Conversia.API.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Conversia.IntegrationTests;

/// <summary>
/// <c>Privacy:*</c> (docs/USERS_TENANTS_PERMISSIONS.md, "Contact suppression"): required outside Development, a fixed non-secret key with a
/// warning in Development, validated at startup naming the setting and never its value. These tests never touch the database.
/// </summary>
public class PrivacyConfigurationTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Conversia.API";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void WithoutAKey_Development_UsesTheMarkedFallback_AndProduction_Refuses()
    {
        Assert.True(PrivacyServiceCollectionExtensions.GetValidatedKeys(Config(), new Env(Environments.Development)).IsDevelopmentFallback);

        var failure = Assert.Throws<InvalidOperationException>(() => PrivacyServiceCollectionExtensions.GetValidatedKeys(Config(), new Env(Environments.Production)));
        Assert.Contains("Privacy:HashKey", failure.Message);
    }

    [Theory]
    [InlineData("not base64 !!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")] // 16 bytes
    public void AMalformedKey_StopsTheApi_NamingTheSettingAndNeverItsValue(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            PrivacyServiceCollectionExtensions.GetValidatedKeys(Config(("Privacy:HashKey", value)), new Env(Environments.Production)));

        Assert.Contains("Privacy:HashKey", failure.Message);
        Assert.DoesNotContain(value, failure.Message);
    }

    [Fact]
    public void TheKeyId_AndTheRetiredKeys_AreRead_AndAReusedIdIsRefused()
    {
        var keys = PrivacyServiceCollectionExtensions.GetValidatedKeys(
            Config(("Privacy:HashKey", Key), ("Privacy:HashKeyId", "2026b"), ("Privacy:RetiredHashKeys:2026a", Key)), new Env(Environments.Production));

        Assert.Equal("2026b", keys.CurrentId);
        Assert.Equal(["2026a"], keys.Retired.Keys);
        Assert.False(keys.IsDevelopmentFallback);
        Assert.Throws<InvalidOperationException>(() => PrivacyServiceCollectionExtensions.GetValidatedKeys(
            Config(("Privacy:HashKey", Key), ("Privacy:HashKeyId", "x"), ("Privacy:RetiredHashKeys:x", Key)), new Env(Environments.Production)));
        Assert.Throws<InvalidOperationException>(() => PrivacyServiceCollectionExtensions.GetValidatedKeys(
            Config(("Privacy:HashKey", Key), ("Privacy:RetiredHashKeys:old", "short")), new Env(Environments.Production)));
    }

    private sealed class ProductionWithoutKey : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            builder.UseSetting("Jwt:SigningKey", TestJwtSettings.SigningKey);
        }
    }

    [Fact]
    public void TheApi_DoesNotStartOutsideDevelopmentWithoutTheKey()
    {
        using var factory = new ProductionWithoutKey();

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        var chain = new List<string>();
        for (var e = failure; e is not null; e = e.InnerException)
        {
            chain.Add(e.Message);
        }

        Assert.Contains(chain, m => m.Contains("Privacy:HashKey", StringComparison.Ordinal));
    }
}
