using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Conversia.IntegrationTests;

/// <summary>
/// The messaging settings are validated when the API starts: a malformed value stops it with an <see cref="InvalidOperationException"/>
/// that names the setting and never prints its value. A missing master key is not an error (the API runs without the credentials
/// endpoints). These tests never touch the database.
/// </summary>
public class MessagingConfigurationTests
{
    private const string KeySetting = "Messaging:CredentialEncryptionKey";

    private sealed class SettingsFactory(params (string Key, string Value)[] settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Jwt:SigningKey", TestJwtSettings.SigningKey);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }

    private static InvalidOperationException? StartupErrorNaming(Exception failure, string setting)
    {
        for (var e = failure; e is not null; e = e.InnerException)
        {
            if (e is InvalidOperationException invalid && invalid.Message.Contains(setting, StringComparison.Ordinal))
            {
                return invalid;
            }
        }

        return null;
    }

    public static TheoryData<string, string> MalformedKeys => new()
    {
        { "not-base64-at-all-!!!", "not base64" },
        { "REPLACE_WITH_A_BASE64_KEY_OF_32_RANDOM_BYTES", "a placeholder" },
        { Convert.ToBase64String(new byte[16]), "16 bytes" },
        { Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(31)), "31 bytes" },
        { Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(33)), "33 bytes" },
        { Convert.ToBase64String(new byte[32]), "32 identical bytes" },
        { "0123456789abcdef0123456789abcdef", "32 characters that are not the base64 of 32 bytes" },
    };

    [Theory]
    [MemberData(nameof(MalformedKeys))]
    public void AMalformedMasterKey_StopsTheApi_NamingTheSettingAndNeverItsValue(string key, string reason)
    {
        using var factory = new SettingsFactory((KeySetting, key));

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.True(failure is not null, $"The host started with a malformed master key ({reason}).");
        Assert.True(StartupErrorNaming(failure!, KeySetting) is not null, $"Expected an InvalidOperationException naming '{KeySetting}' ({reason}); got {failure!.GetType().Name}.");
        for (var e = failure; e is not null; e = e.InnerException)
        {
            Assert.DoesNotContain(key, e.ToString());
        }
    }

    [Theory]
    [InlineData("Messaging:CredentialEncryptionKeyId", "has spaces")]
    [InlineData("Messaging:CredentialEncryptionKeyId", "dotted.id")]
    [InlineData("Messaging:SendRateLimitPerMinute", "0")]
    [InlineData("Messaging:TenantSendRateLimitPerMinute", "0")]
    [InlineData("Messaging:TenantSendRateLimitPerMinute", "many")]
    [InlineData("Messaging:RecordTimeoutSeconds", "0")]
    [InlineData("Messaging:RecordTimeoutSeconds", "301")]
    [InlineData("Dialog360:SandboxBaseUrl", "not a url")]
    [InlineData("Dialog360:SandboxBaseUrl", "ftp://waba-sandbox.360dialog.io")]
    [InlineData("Dialog360:ProductionBaseUrl", "http://waba-v2.360dialog.io")] // plain http would send the key in clear
    [InlineData("Dialog360:ProductionBaseUrl", "https://user:password@waba-v2.360dialog.io")]
    [InlineData("Dialog360:ProductionBaseUrl", "https://waba-v2.360dialog.io/?key=value")]
    [InlineData("Dialog360:TimeoutSeconds", "0")]
    [InlineData("Dialog360:TimeoutSeconds", "ten")]
    public void AnInvalidMessagingSetting_StopsTheApi_NamingIt(string setting, string value)
    {
        using var factory = new SettingsFactory((setting, value));

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        Assert.NotNull(StartupErrorNaming(failure!, setting));
    }

    [Fact]
    public void WithoutAMasterKey_OrWithAValidOne_TheApiStarts()
    {
        using var without = new SettingsFactory();
        using var blank = new SettingsFactory((KeySetting, "  "));
        using var with = new SettingsFactory(
            (KeySetting, TestHelpers.TestEncryptionKey), ("Messaging:CredentialEncryptionKeyId", "2026-11"),
            ("Dialog360:SandboxBaseUrl", "http://localhost:9911/"), ("Dialog360:TimeoutSeconds", "30"));

        Assert.Null(Record.Exception(() => without.CreateClient()));
        Assert.Null(Record.Exception(() => blank.CreateClient()));
        Assert.Null(Record.Exception(() => with.CreateClient()));
    }
}
