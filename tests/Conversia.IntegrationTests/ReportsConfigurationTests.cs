using Conversia.API.Configuration;
using Conversia.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Conversia.IntegrationTests;

/// <summary>The email sender identity and the reports' settings are validated at startup (docs/REPORTS.md, "Configuration"): a bad value names its key, never itself.</summary>
public class ReportsConfigurationTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Conversia.API";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static readonly Env Production = new(Environments.Production);
    private static readonly Env Development = new(Environments.Development);

    [Fact]
    public void Without_a_provider_no_email_is_sent()
        => Assert.False(ReportsServiceCollectionExtensions.GetValidatedEmailOptions(Config(), Production).Enabled);

    [Fact]
    public void A_valid_smtp_server_requires_tls_and_a_sender()
    {
        var options = ReportsServiceCollectionExtensions.GetValidatedEmailOptions(Config(
            ("Email:Provider", "Smtp"), ("Email:FromAddress", "informes@conversia.test"), ("Email:FromName", "Conversia"), ("Email:Smtp:Host", "smtp.example.test")), Production);

        Assert.Equal(("informes@conversia.test", "smtp.example.test", 587, SmtpOptions.StartTls), (options.FromAddress, options.Smtp!.Host, options.Smtp.Port, options.Smtp.Security));
    }

    [Theory]
    [InlineData("Email:FromAddress", "no-es-correo")]
    [InlineData("Email:Smtp:Security", "None")]
    [InlineData("Email:Smtp:Host", "")]
    [InlineData("Email:Smtp:Port", "70000")]
    [InlineData("Email:FromName", "Línea\nBcc: x@example.test")]
    public void A_bad_value_stops_the_api_naming_the_key_and_never_the_value(string key, string value)
    {
        var values = new Dictionary<string, string>
        {
            ["Email:Provider"] = "Smtp", ["Email:FromAddress"] = "informes@conversia.test", ["Email:Smtp:Host"] = "smtp.example.test", [key] = value,
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ReportsServiceCollectionExtensions.GetValidatedEmailOptions(Config([.. values.Select(v => (v.Key, v.Value))]), Production));

        Assert.Contains(key, error.Message);
        if (value.Length > 4)
        {
            Assert.DoesNotContain(value, error.Message);
        }
    }

    [Fact]
    public void A_user_name_needs_its_password_and_the_eml_folder_is_for_development_only()
    {
        Assert.Contains("Email:Smtp:Password", Assert.Throws<InvalidOperationException>(() => ReportsServiceCollectionExtensions.GetValidatedEmailOptions(Config(
            ("Email:Provider", "Smtp"), ("Email:FromAddress", "informes@conversia.test"), ("Email:Smtp:Host", "smtp.example.test"), ("Email:Smtp:UserName", "usuario")), Production)).Message);

        var pickup = Config(("Email:Provider", "Pickup"), ("Email:FromAddress", "informes@conversia.test"), ("Email:Pickup:Directory", "/tmp/eml"));
        Assert.Throws<InvalidOperationException>(() => ReportsServiceCollectionExtensions.GetValidatedEmailOptions(pickup, Production));
        Assert.Equal("/tmp/eml", ReportsServiceCollectionExtensions.GetValidatedEmailOptions(pickup, Development).PickupDirectory);
    }

    [Theory]
    [InlineData("http://app.example.test", false)]
    [InlineData("https://app.example.test/?x=1", false)]
    [InlineData("ftp://app.example.test", false)]
    [InlineData("https://app.example.test", true)]
    [InlineData("http://localhost:5112", true)]
    public void The_web_address_of_the_links_must_be_https_outside_a_local_machine(string url, bool accepted)
    {
        var read = () => ReportsServiceCollectionExtensions.GetValidatedSettings(Config(("Reports:WebBaseUrl", url)), Production);
        if (accepted)
        {
            Assert.StartsWith(url.TrimEnd('/'), read().WebBaseUrl.ToString());
        }
        else
        {
            Assert.Contains("Reports:WebBaseUrl", Assert.Throws<InvalidOperationException>(read).Message);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost:5112")]
    [InlineData("http://app.example.test")]
    public void Outside_development_a_deployment_that_sends_email_needs_an_https_web_address(string? url)
    {
        var values = new List<(string, string)> { ("Email:Provider", "Smtp") };
        if (url is not null)
        {
            values.Add(("Reports:WebBaseUrl", url));
        }

        Assert.Contains("Reports:WebBaseUrl", Assert.Throws<InvalidOperationException>(() =>
            ReportsServiceCollectionExtensions.GetValidatedSettings(Config([.. values]), Production)).Message);
        Assert.Equal("https://app.example.test/",
            ReportsServiceCollectionExtensions.GetValidatedSettings(Config(("Email:Provider", "Smtp"), ("Reports:WebBaseUrl", "https://app.example.test")), Production).WebBaseUrl.ToString());
    }

    [Fact]
    public void A_dedicated_unsubscribe_key_is_used_when_set_and_must_be_long_enough()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);

        Assert.Equal(key, ReportsServiceCollectionExtensions.GetUnsubscribeKey(Config(("Reports:UnsubscribeKey", Convert.ToBase64String(key)))));
        Assert.Contains("Reports:UnsubscribeKey", Assert.Throws<InvalidOperationException>(() =>
            ReportsServiceCollectionExtensions.GetUnsubscribeKey(Config(("Reports:UnsubscribeKey", Convert.ToBase64String(new byte[16]))))).Message);
    }
}
