using System.Net;
using System.Net.Http.Json;
using Conversia.API.Configuration;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The first platform administrator comes from configuration (PlatformAdmin:Email and :Password): created once, never overwritten,
/// refused when weak, and never written to a log.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class PlatformAdminBootstrapTests
{
    private const string GoodPassword = "Bootstrap-Passw0rd-xyz";

    private readonly CustomWebApplicationFactory _factory;

    public PlatformAdminBootstrapTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    // ---- the validator ----

    [Fact]
    public void NothingConfigured_MeansNoBootstrap_AndTheDefaultTokenLifetime()
    {
        var options = PlatformAdminOptionsValidator.GetValidatedOptions(Config());

        Assert.False(options.BootstrapRequested);
        Assert.Equal(60, options.TokenMinutes);
    }

    [Fact]
    public void BlankValues_AreTreatedAsNothingConfigured()
        => Assert.False(PlatformAdminOptionsValidator.GetValidatedOptions(Config(("PlatformAdmin:Email", ""), ("PlatformAdmin:Password", ""))).BootstrapRequested);

    [Fact]
    public void AGoodPair_IsAccepted()
    {
        var options = PlatformAdminOptionsValidator.GetValidatedOptions(Config(("PlatformAdmin:Email", " boss@example.test "), ("PlatformAdmin:Password", GoodPassword), ("PlatformAdmin:TokenMinutes", "15")));

        Assert.True(options.BootstrapRequested);
        Assert.Equal("boss@example.test", options.Email);
        Assert.Equal(15, options.TokenMinutes);
    }

    [Theory]
    [InlineData("short1!")]
    [InlineData("12345678901")]
    [InlineData("change_me")]
    [InlineData("change_me_change_me")]
    [InlineData("CHANGE-ME-please-now")]
    [InlineData("ChangeMe12345678")]
    [InlineData("REPLACE_WITH_A_STRONG_PASSWORD")]
    [InlineData("placeholder-password")]
    [InlineData("aaaaaaaaaaaaaaaaaaaa")]
    public void AWeakOrPlaceholderPassword_FailsNamingTheKeyAndNeverPrintingIt(string password)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            PlatformAdminOptionsValidator.GetValidatedOptions(Config(("PlatformAdmin:Email", "boss@example.test"), ("PlatformAdmin:Password", password))));

        Assert.Contains("PlatformAdmin:Password", failure.Message);
        Assert.DoesNotContain(password, failure.Message);
    }

    [Theory]
    [InlineData("boss@example.test", null, "PlatformAdmin:Password")]
    [InlineData(null, GoodPassword, "PlatformAdmin:Email")]
    [InlineData("not-an-email", GoodPassword, "PlatformAdmin:Email")]
    public void HalfAPair_OrABadEmail_FailsNamingTheKey(string? email, string? password, string key)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            PlatformAdminOptionsValidator.GetValidatedOptions(Config(("PlatformAdmin:Email", email), ("PlatformAdmin:Password", password))));

        Assert.Contains(key, failure.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("721")]
    [InlineData("soon")]
    public void ABadTokenLifetime_FailsNamingTheKey(string minutes)
        => Assert.Contains("PlatformAdmin:TokenMinutes", Assert.Throws<InvalidOperationException>(() =>
            PlatformAdminOptionsValidator.GetValidatedOptions(Config(("PlatformAdmin:TokenMinutes", minutes)))).Message);

    // ---- the password from a file (a Docker secret) ----

    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"conversia-test-{Guid.NewGuid():N}.secret");
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ThePasswordCanComeFromAFile_WithItsTrailingNewlineDropped(string ending)
    {
        var file = TempFile(GoodPassword + ending);
        try
        {
            var options = PlatformAdminOptionsValidator.GetValidatedOptions(Config(("PlatformAdmin:Email", "boss@example.test"), ("PlatformAdmin:PasswordFile", file)));

            Assert.True(options.BootstrapRequested);
            Assert.Equal(GoodPassword, options.Password);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AnUnreadableOrEmptyFile_FailsNamingTheKeyAndNeverThePath()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"conversia-missing-{Guid.NewGuid():N}.secret");
        var empty = TempFile("\n");
        try
        {
            var unreadable = Assert.Throws<InvalidOperationException>(() =>
                PlatformAdminOptionsValidator.GetValidatedOptions(Config(("PlatformAdmin:Email", "boss@example.test"), ("PlatformAdmin:PasswordFile", missing))));
            var blank = Assert.Throws<InvalidOperationException>(() =>
                PlatformAdminOptionsValidator.GetValidatedOptions(Config(("PlatformAdmin:Email", "boss@example.test"), ("PlatformAdmin:PasswordFile", empty))));

            Assert.Contains("PlatformAdmin:PasswordFile", unreadable.Message);
            Assert.DoesNotContain(missing, unreadable.Message);
            Assert.Contains("PlatformAdmin:PasswordFile", blank.Message);
            Assert.DoesNotContain(empty, blank.Message);
        }
        finally
        {
            File.Delete(empty);
        }
    }

    [Fact]
    public void AFileAndAPasswordTogether_AreAMistake_AndAWeakPasswordInAFileIsRefusedLikeAnyOther()
    {
        var file = TempFile("change_me\n");
        try
        {
            var both = Assert.Throws<InvalidOperationException>(() =>
                PlatformAdminOptionsValidator.GetValidatedOptions(Config(("PlatformAdmin:Email", "boss@example.test"), ("PlatformAdmin:Password", GoodPassword), ("PlatformAdmin:PasswordFile", file))));
            var weak = Assert.Throws<InvalidOperationException>(() =>
                PlatformAdminOptionsValidator.GetValidatedOptions(Config(("PlatformAdmin:Email", "boss@example.test"), ("PlatformAdmin:PasswordFile", file))));

            Assert.Contains("PlatformAdmin:PasswordFile", both.Message);
            Assert.DoesNotContain(GoodPassword, both.Message);
            Assert.Contains("PlatformAdmin:Password", weak.Message);
            Assert.DoesNotContain("change_me", weak.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AFileAloneWithoutAnEmail_IsHalfAPair()
    {
        var file = TempFile(GoodPassword);
        try
        {
            var failure = Assert.Throws<InvalidOperationException>(() => PlatformAdminOptionsValidator.GetValidatedOptions(Config(("PlatformAdmin:PasswordFile", file))));

            Assert.Contains("PlatformAdmin:Email", failure.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- the host ----

    [Fact]
    public async Task AfterTheBootstrapTheOptionsAndTheConfigurationNoLongerHoldThePassword()
    {
        await using var database = TestDatabase.Create(_factory);
        await database.MigrateAsync();
        var email = UniqueEmail("boss");
        var file = TempFile(GoodPassword + "\n");
        try
        {
            using var host = _factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("PlatformAdmin:Email", email);
                builder.UseSetting("PlatformAdmin:PasswordFile", file);
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                    services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(database.ConnectionString));
                });
            });
            using var client = host.CreateClient();

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(AdminLoginUrl, new { email, password = GoodPassword })).StatusCode); // created from the file
            var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Conversia.Infrastructure.Configuration.PlatformAdminOptions>>().Value;
            Assert.Null(options.Password);
            Assert.Null(host.Services.GetRequiredService<IConfiguration>()["PlatformAdmin:Password"]);
            Assert.Equal(email, options.Email);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AnExistingAccountAlsoClearsThePasswordTheBootstrapDidNotUse()
    {
        await using var database = TestDatabase.Create(_factory);
        await database.MigrateAsync();
        var email = UniqueEmail("boss");
        using (var first = Host(database, email, GoodPassword, new CapturingLoggerProvider()))
        {
            first.CreateClient();
        }

        using var second = Host(database, email, "Another-Passw0rd-abc", new CapturingLoggerProvider());
        second.CreateClient();

        Assert.Null(second.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Conversia.Infrastructure.Configuration.PlatformAdminOptions>>().Value.Password);
    }

    [Fact]
    public void TheApiRefusesToStartWithAWeakBootstrapPassword()
    {
        using var host = _factory.WithWebHostBuilder(b => b.UseSetting("PlatformAdmin:Email", "boss@example.test").UseSetting("PlatformAdmin:Password", "change_me"));

        var failure = Record.Exception(() => host.CreateClient());

        Assert.NotNull(failure);
        var startupError = Chain(failure!).OfType<InvalidOperationException>().FirstOrDefault(e => e.Message.Contains("PlatformAdmin:Password"));
        Assert.NotNull(startupError);
        Assert.DoesNotContain("change_me", startupError!.Message);
    }

    [Fact]
    public async Task TheAdminIsCreatedOnce_CanSignIn_AndAnExistingOneIsNeverOverwritten_NorLoggedWithSecrets()
    {
        await using var database = TestDatabase.Create(_factory);
        await database.MigrateAsync();
        var email = UniqueEmail("boss");
        var logs = new CapturingLoggerProvider();

        using (var first = Host(database, email, GoodPassword, logs))
        {
            using var client = first.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(AdminLoginUrl, new { email, password = GoodPassword })).StatusCode);
        }

        // Same email, a different configured password: the account exists, so nothing changes.
        const string other = "Another-Passw0rd-abc";
        using (var second = Host(database, email, other, logs))
        {
            using var client = second.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(AdminLoginUrl, new { email, password = GoodPassword })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(AdminLoginUrl, new { email, password = other })).StatusCode);
        }

        await using var db = database.NewContext();
        Assert.Equal(1, await db.PlatformAdmins.CountAsync(a => a.Email == email));

        var all = string.Join("\n", logs.Entries.Select(e => e.Text));
        Assert.Contains("Platform administrator created.", all);
        Assert.Contains("Platform administrator already exists", all);
        foreach (var secret in new[] { GoodPassword, other, email })
        {
            Assert.DoesNotContain(secret, all);
        }
    }

    [Fact]
    public async Task NothingConfigured_TheHostStartsWithoutTouchingTheDatabase()
    {
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(TestDatabase.UnreachableConnectionString));
        }));

        using var client = host.CreateClient(); // would throw if the bootstrapper queried the unreachable database

        await Task.CompletedTask;
        Assert.NotNull(client);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host(TestDatabase database, string email, string password, CapturingLoggerProvider logs)
        => _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("PlatformAdmin:Email", email);
            builder.UseSetting("PlatformAdmin:Password", password);
            builder.ConfigureLogging(l => l.AddProvider(logs));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(database.ConnectionString));
            });
        });

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }
}
