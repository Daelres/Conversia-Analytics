extern alias ApiHost;

using System.Net;
using System.Text.Json;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Configuration;

/// <summary>
/// <c>Demo:ShowAccounts</c> and <c>Demo:Password</c> of the Web: the Web must refuse to boot — with an
/// <see cref="InvalidOperationException"/> naming the setting and never printing a password — when the demo accounts are shown
/// outside the Development/Demo environments or a setting is malformed (same fail-fast style as the API's <c>Demo:Enabled</c>).
/// None of these tests needs a backend: the hosts have none, and everything they check happens before or without an API call.
/// </summary>
public class WebDemoConfigurationTests
{
    private const string ShowAccounts = "Demo:ShowAccounts";
    private const string PasswordSetting = "Demo:Password";
    private const string CustomPassword = "Custom-Pass-2027-Zq";

    private static readonly string[] AllowedEnvironmentFiles = { "appsettings.Development.json", "appsettings.Demo.json" };

    private static Exception? Boot(WebSettings settings)
    {
        using var host = WebTestHost.CreateWithoutApi(settings);
        return Record.Exception(() => host.NewSession());
    }

    private static void AssertBootRefusedNaming(Exception? failure, string setting, string context)
    {
        Assert.True(failure is not null, $"The Web booted although it must refuse to ({context}).");
        var startupError = ExceptionChain.Find<InvalidOperationException>(failure!, e => e.Message.Contains(setting));
        Assert.True(startupError is not null, $"Expected an InvalidOperationException naming '{setting}' ({context}); got: {ExceptionChain.Describe(failure!)}");
    }

    private static void AssertNeverPrinted(Exception failure, string secret)
    {
        foreach (var e in ExceptionChain.Flatten(failure))
        {
            Assert.DoesNotContain(secret, e.Message);
            Assert.DoesNotContain(secret, e.ToString());
        }
    }

    private static async Task<PageResponse> TourAsync(WebSettings settings)
    {
        using var host = WebTestHost.CreateWithoutApi(settings);
        using var session = host.NewSession();
        var page = await session.GetAsync("/demo");
        Assert.Equal(0, host.Api.CallCount);
        return page.ShouldBe(HttpStatusCode.OK);
    }

    // ---- the environment guard ----

    [Theory]
    [InlineData("Development", "true")]
    [InlineData("Development", "True")]
    [InlineData("Demo", "true")]
    [InlineData("Demo", "TRUE")]
    public async Task Showing_the_accounts_boots_in_the_development_and_demo_environments_and_shows_them(string environment, string value)
    {
        var page = await TourAsync(new WebSettings(environment, value));

        Assert.Equal(DemoAccounts.AllEmails.Order(), DemoFlows.TableEmails(page).Order());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Preview")]
    public void Showing_the_accounts_outside_development_and_demo_is_refused_naming_the_setting(string environment)
    {
        var failure = Boot(new WebSettings(environment, "true"));

        AssertBootRefusedNaming(failure, ShowAccounts, $"Demo:ShowAccounts=true in '{environment}'");
        AssertNeverPrinted(failure!, DemoAccounts.Password);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Showing_the_accounts_outside_development_and_demo_never_prints_a_custom_password_either(string environment)
    {
        var failure = Boot(new WebSettings(environment, "true", CustomPassword));

        Assert.NotNull(failure);
        AssertNeverPrinted(failure!, CustomPassword);
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "False")]
    [InlineData("Staging", "false")]
    [InlineData("Development", null)]
    [InlineData("Demo", "false")]
    public async Task With_the_accounts_not_shown_every_environment_boots_and_the_tour_lists_no_account(string environment, string? value)
    {
        var page = await TourAsync(new WebSettings(environment, value));

        DemoFlows.AssertIsTheRealTour(page); // precondition: the real tour, not an empty page
        DemoFlows.AssertNoDemoAccountMaterial(page);
    }

    // ---- malformed values ----

    [Theory]
    [InlineData("yes")]
    [InlineData("tru")]
    [InlineData("2")]
    [InlineData("true, false")]
    [InlineData("null")]
    public void A_non_boolean_value_is_refused_naming_the_setting(string value)
    {
        AssertBootRefusedNaming(Boot(new WebSettings("Development", value)), ShowAccounts, $"Demo:ShowAccounts='{value}' in Development");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Demo")]
    public void A_non_boolean_value_is_refused_in_every_environment(string environment)
    {
        AssertBootRefusedNaming(Boot(new WebSettings(environment, "maybe")), ShowAccounts, $"Demo:ShowAccounts='maybe' in {environment}");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_ambiguous_value_is_either_refused_naming_the_setting_or_shows_no_account(string value)
    {
        // The requirement only calls "yes"/"maybe"-like values non-boolean; whether "1", "0" or a blank value count as
        // booleans is open. A fail-fast boot naming the setting and a plain "not shown" boot are both controlled outcomes.
        // What is not acceptable is any other exception, or accounts and a password shown in a Production deployment.
        var failure = Boot(new WebSettings("Production", value));

        if (failure is not null)
        {
            AssertBootRefusedNaming(failure, ShowAccounts, $"Demo:ShowAccounts='{value}'");
            return;
        }

        DemoFlows.AssertNoDemoAccountMaterial(await TourAsync(new WebSettings("Production", value)));
    }

    // ---- the password ----

    [Theory]
    [InlineData("short")]
    [InlineData("Ab1")]
    [InlineData("alllowercase-with-1-digit")]
    [InlineData("ALLUPPERCASE-WITH-1-DIGIT")]
    [InlineData("No-Digits-Anywhere-Here")]
    public void A_demo_password_that_breaks_the_registration_policy_is_refused_naming_the_setting_and_is_never_printed(string password)
    {
        var failure = Boot(new WebSettings("Demo", "true", password));

        AssertBootRefusedNaming(failure, PasswordSetting, $"Demo:Password='{password}'");
        AssertNeverPrinted(failure!, password);
    }

    [Fact]
    public void A_demo_password_longer_than_the_policy_allows_is_refused_and_never_printed()
    {
        var password = "Aa1-" + new string('x', 130);

        var failure = Boot(new WebSettings("Development", "true", password));

        AssertBootRefusedNaming(failure, PasswordSetting, "a 134-character Demo:Password");
        AssertNeverPrinted(failure!, password);
    }

    [Theory]
    [InlineData("Custom-Pass-2027-Zq")]
    [InlineData("Another-Valid-Pw-42!")]
    public async Task A_demo_password_that_follows_the_policy_boots_and_is_what_the_tour_shows(string password)
    {
        var page = await TourAsync(new WebSettings("Demo", "true", password));

        Assert.All(DemoFlows.DisplayedPasswords(page), p => Assert.Equal(password, p));
        Assert.NotEmpty(DemoFlows.DisplayedPasswords(page));
    }

    [Fact]
    public async Task Without_a_demo_password_the_tour_shows_the_default_one()
    {
        var page = await TourAsync(new WebSettings("Demo", "true"));

        Assert.All(DemoFlows.DisplayedPasswords(page), p => Assert.Equal(DemoAccounts.Password, p));
        Assert.NotEmpty(DemoFlows.DisplayedPasswords(page));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("ALLUPPERCASE-WITH-1-DIGIT")]
    public async Task A_bad_demo_password_while_the_accounts_are_not_shown_is_refused_naming_the_setting_or_ignored_without_exposing_anything(string password)
    {
        // Open in the requirement: the password is only used when the accounts are shown. Refusing early is fine; booting is fine;
        // neither may print the value, and a booting Web must not show accounts.
        var failure = Boot(new WebSettings("Production", "false", password));

        if (failure is not null)
        {
            AssertBootRefusedNaming(failure, PasswordSetting, $"Demo:Password='{password}' with the accounts hidden");
            AssertNeverPrinted(failure, password);
            return;
        }

        var page = await TourAsync(new WebSettings("Production", "false", password));
        DemoFlows.AssertNoDemoAccountMaterial(page, password);
    }

    // ---- shipped defaults ----

    [Fact]
    public void Shipped_appsettings_never_enable_the_demo_accounts_outside_development_and_demo()
    {
        // appsettings.Development.json and appsettings.Demo.json may legitimately turn the accounts on (the only environments that allow it).
        var directory = RepoPaths.WebProject();
        var files = Directory.GetFiles(directory, "appsettings*.json")
            .Where(f => !AllowedEnvironmentFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .ToArray();

        Assert.Contains(files, f => Path.GetFileName(f) == "appsettings.json");
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.TryGetProperty("Demo", out var demo) && demo.TryGetProperty("ShowAccounts", out var shown))
            {
                var isOff = shown.ValueKind == JsonValueKind.False
                    || (shown.ValueKind == JsonValueKind.String && shown.GetString()!.Equals("false", StringComparison.OrdinalIgnoreCase));
                Assert.True(isOff, $"{Path.GetFileName(file)} must not turn the demo accounts on: Demo:ShowAccounts is opt-in.");
            }
        }
    }
}
