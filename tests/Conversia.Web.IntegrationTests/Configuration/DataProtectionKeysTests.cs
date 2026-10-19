extern alias ApiHost;

using System.Net;
using Conversia.Web.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Configuration;

/// <summary>
/// <c>DataProtection:KeysPath</c> of the Web: with it the key ring is written to that directory, so a second server process
/// (a restarted container) still reads what the first one protected — the sign-in cookie and the antiforgery token of a form
/// that was open before the restart. Without it the framework default stays.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public sealed class DataProtectionKeysTests(TestApiFactory api) : IDisposable
{
    private const string KeysPath = "DataProtection:KeysPath";

    private readonly string _directory = Directory.CreateTempSubdirectory("conversia-keys-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private WebSettings Settings(string? path = null) => WebSettings.With((KeysPath, path ?? _directory));

    [Fact]
    public void The_keys_are_written_to_the_configured_directory_and_a_second_host_reads_what_the_first_protected()
    {
        const string Purpose = "Conversia.Tests.KeysPath";
        string protectedPayload;
        using (var first = WebTestHost.CreateWithoutApi(Settings()))
        {
            protectedPayload = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("hello");
        }

        Assert.NotEmpty(Directory.GetFiles(_directory, "key-*.xml"));

        using var second = WebTestHost.CreateWithoutApi(Settings());
        Assert.Equal("hello", second.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(protectedPayload));
    }

    [Fact]
    public async Task A_form_opened_on_one_host_can_be_submitted_to_another_host_sharing_the_keys()
    {
        using var first = WebTestHost.Create(api, settings: Settings());
        using var before = first.NewSession();
        var page = (await before.GetAsync("/login")).ShouldBe(HttpStatusCode.OK);
        var form = FormModel.From(page, page.FindPasswordForm()).Set("email", ApiSeed.UniqueEmail("keys")).Set("password", "Wr0ng-Pass-Value-123");

        using var second = WebTestHost.Create(api, settings: Settings());
        using var after = second.NewSession();
        after.CopyCookiesFrom(before);
        var response = await after.SubmitAsync(form);

        // The antiforgery check passes: the answer is the login page again (wrong credentials), never a 400.
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(SpanishText.InvalidCredentials, response.Text);
    }

    [Fact]
    public void Without_a_configured_path_the_host_boots_with_the_framework_default_and_writes_nothing_to_the_directory()
    {
        using var host = WebTestHost.CreateWithoutApi(new WebSettings());

        Assert.NotNull(host.Services.GetRequiredService<IDataProtectionProvider>());
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public void A_directory_that_does_not_exist_is_refused_naming_the_setting()
    {
        var missing = Path.Combine(_directory, "missing");

        var failure = Record.Exception(() => WebTestHost.CreateWithoutApi(Settings(missing)).NewSession());

        Assert.True(failure is not null, "The Web booted although the keys directory does not exist.");
        Assert.NotNull(ExceptionChain.Find<InvalidOperationException>(failure!, e => e.Message.Contains(KeysPath) && e.Message.Contains(missing)));
    }

    [Fact]
    public void A_directory_the_web_cannot_write_to_is_refused_naming_the_setting()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // directory permissions do not stop the file system here
        }

        File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var failure = Record.Exception(() => WebTestHost.CreateWithoutApi(Settings()).NewSession());

            Assert.True(failure is not null, "The Web booted although it cannot write to the keys directory.");
            Assert.NotNull(ExceptionChain.Find<InvalidOperationException>(failure!, e => e.Message.Contains(KeysPath) && e.Message.Contains("cannot write")));
        }
        finally
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
