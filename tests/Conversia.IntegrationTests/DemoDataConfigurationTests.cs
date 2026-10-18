using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.IntegrationTests;

/// <summary>
/// Configuration of the opt-in demo data (<c>Demo:Enabled</c>, <c>Demo:Password</c>) and its environment guard:
/// the API must refuse to boot — with an <see cref="InvalidOperationException"/> naming the setting and never printing
/// a password — when demo seeding is requested in a hosting environment other than Development/Demo, or when a
/// setting is malformed (same style as <see cref="JwtSigningKeyStartupTests"/>). Every failure here happens before any
/// database access, so none of these tests need a database: the hosts point at one nobody listens on. The class still
/// joins the integration collection so that its many short-lived hosts never run next to the database suites.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class DemoDataConfigurationTests
{
    private const string EnabledSetting = "Demo:Enabled";
    private const string PasswordSetting = "Demo:Password";

    private static DemoHost HostWith(string? enabled, string? password = null, string environment = "Development")
        => new(new DemoHostSettings(enabled, password, environment));

    private static Exception? BootFailure(DemoHost host) => Record.Exception(() => host.Start());

    private static void AssertBootRefusedNaming(Exception? failure, string setting, string context)
    {
        Assert.True(failure is not null, $"Host booted although it must refuse to ({context}).");
        var startupError = ExceptionChain.Find<InvalidOperationException>(failure!, e => e.Message.Contains(setting));
        Assert.True(
            startupError is not null,
            $"Expected an InvalidOperationException naming '{setting}' ({context}); got: {ExceptionChain.Describe(failure!)}");
    }

    private static void AssertNeverPrinted(Exception failure, string secret)
    {
        foreach (var e in ExceptionChain.Flatten(failure))
        {
            Assert.DoesNotContain(secret, e.Message);
            Assert.DoesNotContain(secret, e.ToString());
        }
    }

    // ---- The environment guard -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    [InlineData("QA")]
    [InlineData("Preview")]
    public void Boot_WithDemoEnabledOutsideDevelopmentAndDemo_IsRefusedNamingTheSetting(string environment)
    {
        using var host = HostWith("true", environment: environment);

        var failure = BootFailure(host);

        AssertBootRefusedNaming(failure, EnabledSetting, $"Demo:Enabled=true in '{environment}'");
        AssertNeverPrinted(failure!, DemoData.DefaultPassword);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Boot_WithDemoEnabledOutsideDevelopmentAndDemo_NeverPrintsACustomPasswordEither(string environment)
    {
        const string password = "Custom-Pass-2027-Zq";
        using var host = HostWith("true", password, environment);

        var failure = BootFailure(host);

        Assert.NotNull(failure);
        AssertNeverPrinted(failure!, password);
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "false")]
    [InlineData("Production", "False")]
    [InlineData("Staging", null)]
    [InlineData("Staging", "false")]
    public void Boot_WithDemoDisabledOrAbsentOutsideDevelopmentAndDemo_StillWorks(string environment, string? enabled)
    {
        using var host = HostWith(enabled, environment: environment);

        var failure = BootFailure(host);

        Assert.True(failure is null, $"A real deployment without demo data must boot: {failure}");
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("maybe")]
    [InlineData("on")]
    [InlineData("y")]
    [InlineData("tru")]
    [InlineData("truee")]
    [InlineData("enabled")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("true;false")]
    [InlineData("true, false")]
    [InlineData("null")]
    public void Boot_WithNonBooleanEnabledValue_IsRefusedNamingTheSetting(string value)
    {
        using var host = HostWith(value);

        AssertBootRefusedNaming(BootFailure(host), EnabledSetting, $"Demo:Enabled='{value}' in Development");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Demo")]
    public void Boot_WithNonBooleanEnabledValue_IsRefusedInEveryEnvironment(string environment)
    {
        using var host = HostWith("maybe", environment: environment);

        AssertBootRefusedNaming(BootFailure(host), EnabledSetting, $"Demo:Enabled='maybe' in {environment}");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("   ")]
    public void Boot_WithAmbiguousEnabledValue_EitherIsRefusedNamingTheSettingOrNothingIsSeeded(string value)
    {
        // The requirement only calls "yes"/"maybe"-like values non-boolean; whether "1", "0" or a blank value count as
        // booleans is open. Both a fail-fast boot naming the setting and a plain "disabled" boot are controlled
        // outcomes. What is not acceptable is any other exception, or demo data in a database nobody asked to seed.
        using var host = HostWith(value, environment: "Production");

        var failure = BootFailure(host);

        if (failure is not null)
        {
            AssertBootRefusedNaming(failure, EnabledSetting, $"Demo:Enabled='{value}'");
        }
        else
        {
            Assert.Equal(0, host.DbCommands.Count);
        }
    }

    // ---- Demo:Password -------------------------------------------------------------------------------------------

    public static TheoryData<string, string> PasswordsBreakingThePolicy => new()
    {
        { "Short1aA", "8 characters" },
        { "Abcdefgh1jk", "11 characters (one below the minimum)" },
        { "Abcdefghijkl", "12 characters but no digit" },
        { "abcdefghij1k", "12 characters but no uppercase letter" },
        { "ABCDEFGHIJ1K", "12 characters but no lowercase letter" },
        { "NoDigitsHereAtAll", "no digit" },
        { "alllowercase123456", "no uppercase letter" },
        { "ALLUPPERCASE123456", "no lowercase letter" },
        { "            ", "12 spaces" },
        { "Aa1" + new string('x', 126), "129 characters (one above the maximum)" },
        { "Aa1" + new string('x', 500), "503 characters" },
    };

    [Theory]
    [MemberData(nameof(PasswordsBreakingThePolicy))]
    public void Boot_WithPasswordBreakingTheRegistrationPolicy_IsRefusedNamingThePasswordSetting(string password, string reason)
    {
        using var host = HostWith("true", password);

        var failure = BootFailure(host);

        AssertBootRefusedNaming(failure, PasswordSetting, $"Demo:Password with {reason}");
        AssertNeverPrinted(failure!, password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Boot_WithBlankPassword_EitherIsRefusedNamingThePasswordSettingOrFallsBackToTheDefault(string password)
    {
        // A blank override is not covered by the requirement: it may be read as "not set" (the default password) or
        // rejected as a policy violation. Either is controlled; any other exception is not.
        using var host = HostWith("true", password);

        var failure = BootFailure(host);

        if (failure is not null)
        {
            AssertBootRefusedNaming(failure, PasswordSetting, $"Demo:Password='{password}'");
        }
    }

    [Theory]
    [InlineData("short")]
    [InlineData("alllowercase123456")]
    public void Boot_WithDemoDisabledAndAnInvalidPassword_EitherBootsOrIsRefusedNamingThePasswordSetting(string password)
    {
        // The password only matters when demo data is on; refusing to boot over an unused setting is allowed but
        // not required. Whatever happens must be controlled and must not echo the value.
        using var host = HostWith("false", password, environment: "Production");

        var failure = BootFailure(host);

        if (failure is not null)
        {
            AssertBootRefusedNaming(failure, PasswordSetting, $"Demo:Enabled=false with Demo:Password='{password}'");
            AssertNeverPrinted(failure, password);
        }
    }

    // ---- Acceptable configurations boot even with no database ----------------------------------------------------

    [Theory]
    [InlineData("Development", "true")]
    [InlineData("Demo", "true")]
    [InlineData("Development", "True")]
    [InlineData("Demo", "TRUE")]
    [InlineData("Development", "false")]
    [InlineData("Demo", null)]
    public async Task Boot_WithAllowedEnvironmentAndNoDatabase_StillServesRequests(string environment, string? enabled)
    {
        using var host = HostWith(enabled, environment: environment);
        using var client = host.CreateClient();

        using var response = await client.PostAsync("/api/auth/login", new StringContent("{not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("Aa1" + "xxxxxxxxx")]
    [InlineData("Custom-Pass-2027")]
    public void Boot_WithPasswordMeetingThePolicy_Builds(string password)
    {
        Assert.InRange(password.Length, 12, 128);
        using var host = HostWith("true", password);

        var failure = BootFailure(host);

        Assert.True(failure is null, $"A policy-conforming Demo:Password must not stop the API from booting: {failure}");
    }

    // ---- Shipped defaults ------------------------------------------------------------------------------------------

    [Fact]
    public void ShippedAppSettings_NeverEnableTheDemoData()
    {
        using var host = HostWith(enabled: null);
        var contentRoot = host.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;

        foreach (var file in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(contentRoot, file)));
            if (document.RootElement.TryGetProperty("Demo", out var demo) && demo.TryGetProperty("Enabled", out var enabled))
            {
                var isOff = enabled.ValueKind == JsonValueKind.False
                    || (enabled.ValueKind == JsonValueKind.String && enabled.GetString()!.Equals("false", StringComparison.OrdinalIgnoreCase));
                Assert.True(isOff, $"{file} must not turn the demo data on: Demo:Enabled is opt-in.");
            }
        }
    }
}
