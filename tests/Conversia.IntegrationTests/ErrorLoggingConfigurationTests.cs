using System.Text.Json;
using Conversia.API.Configuration;
using Conversia.Infrastructure.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Conversia.IntegrationTests;

/// <summary>
/// Settings of <c>ErrorLogging</c>: validated at startup in the style of the other validators (an
/// <see cref="InvalidOperationException"/> naming the key, never printing the report key), defaults when absent. None of this needs
/// the SQL container: the hosts point at a database nobody listens on, which is fine because they fail (or build) before any query.
/// </summary>
public class ErrorLoggingConfigurationTests
{
    private const string GoodKey = "0123456789abcdef0123456789abcdef-report-key";

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    // ---- the validator ----

    [Fact]
    public void Absent_settings_give_the_documented_defaults_and_no_report_key()
    {
        var options = ErrorLoggingOptionsValidator.GetValidatedOptions(Config());

        Assert.True(options.Enabled);
        Assert.Equal(90, options.RetentionDays);
        Assert.Equal(20, options.MaxPerFingerprintPerMinute);
        Assert.Equal(3, options.PersistTimeoutSeconds);
        Assert.Equal(60, options.ReportRateLimitPerMinute);
        Assert.True(string.IsNullOrEmpty(options.ReportKey));
        Assert.Equal(ErrorLoggingOptions.Default.RetentionDays, options.RetentionDays);
    }

    [Fact]
    public void Blank_settings_are_treated_as_absent_like_in_the_other_validators()
    {
        var options = ErrorLoggingOptionsValidator.GetValidatedOptions(Config(
            ("ErrorLogging:Enabled", ""), ("ErrorLogging:RetentionDays", " "), ("ErrorLogging:MaxPerFingerprintPerMinute", ""),
            ("ErrorLogging:PersistTimeoutSeconds", "  "), ("ErrorLogging:ReportRateLimitPerMinute", ""), ("ErrorLogging:ReportKey", "")));

        Assert.Equal(ErrorLoggingOptions.Default.Enabled, options.Enabled);
        Assert.Equal(90, options.RetentionDays);
        Assert.Equal(20, options.MaxPerFingerprintPerMinute);
        Assert.Equal(3, options.PersistTimeoutSeconds);
        Assert.Equal(60, options.ReportRateLimitPerMinute);
        Assert.True(string.IsNullOrEmpty(options.ReportKey));
    }

    [Fact]
    public void Configured_values_are_read_as_given()
    {
        var options = ErrorLoggingOptionsValidator.GetValidatedOptions(Config(
            ("ErrorLogging:Enabled", "false"), ("ErrorLogging:RetentionDays", "30"), ("ErrorLogging:MaxPerFingerprintPerMinute", "7"),
            ("ErrorLogging:PersistTimeoutSeconds", "12"), ("ErrorLogging:ReportRateLimitPerMinute", "120"), ("ErrorLogging:ReportKey", GoodKey)));

        Assert.False(options.Enabled);
        Assert.Equal(30, options.RetentionDays);
        Assert.Equal(7, options.MaxPerFingerprintPerMinute);
        Assert.Equal(12, options.PersistTimeoutSeconds);
        Assert.Equal(120, options.ReportRateLimitPerMinute);
        Assert.Equal(GoodKey, options.ReportKey);
    }

    public static TheoryData<string, string> AcceptedBoundaries => new()
    {
        { "ErrorLogging:RetentionDays", "1" }, { "ErrorLogging:RetentionDays", "3650" },
        { "ErrorLogging:MaxPerFingerprintPerMinute", "1" }, { "ErrorLogging:MaxPerFingerprintPerMinute", "2147483647" },
        { "ErrorLogging:PersistTimeoutSeconds", "1" }, { "ErrorLogging:PersistTimeoutSeconds", "30" },
        { "ErrorLogging:ReportRateLimitPerMinute", "1" }, { "ErrorLogging:ReportRateLimitPerMinute", "2147483647" },
        { "ErrorLogging:ReportKey", "01234567890123456789012345678901" },
        { "ErrorLogging:Enabled", "true" }, { "ErrorLogging:Enabled", "True" }, { "ErrorLogging:Enabled", "FALSE" },
    };

    [Theory]
    [MemberData(nameof(AcceptedBoundaries))]
    public void Boundary_values_that_are_allowed_are_accepted(string key, string value)
    {
        var failure = Record.Exception(() => ErrorLoggingOptionsValidator.GetValidatedOptions(Config((key, value))));

        Assert.Null(failure);
    }

    public static TheoryData<string, string> RejectedValues => new()
    {
        { "ErrorLogging:RetentionDays", "0" }, { "ErrorLogging:RetentionDays", "-1" }, { "ErrorLogging:RetentionDays", "3651" },
        { "ErrorLogging:RetentionDays", "abc" }, { "ErrorLogging:RetentionDays", "1.5" }, { "ErrorLogging:RetentionDays", "99999999999" },
        { "ErrorLogging:RetentionDays", "ninety" }, { "ErrorLogging:RetentionDays", "1,5" },
        { "ErrorLogging:MaxPerFingerprintPerMinute", "0" }, { "ErrorLogging:MaxPerFingerprintPerMinute", "-3" },
        { "ErrorLogging:MaxPerFingerprintPerMinute", "many" }, { "ErrorLogging:MaxPerFingerprintPerMinute", "2.5" },
        { "ErrorLogging:PersistTimeoutSeconds", "0" }, { "ErrorLogging:PersistTimeoutSeconds", "31" }, { "ErrorLogging:PersistTimeoutSeconds", "-1" },
        { "ErrorLogging:PersistTimeoutSeconds", "soon" }, { "ErrorLogging:PersistTimeoutSeconds", "3.5" },
        { "ErrorLogging:ReportRateLimitPerMinute", "0" }, { "ErrorLogging:ReportRateLimitPerMinute", "-60" },
        { "ErrorLogging:ReportRateLimitPerMinute", "lots" }, { "ErrorLogging:ReportRateLimitPerMinute", "6.0" },
        { "ErrorLogging:ReportKey", "short-key" }, { "ErrorLogging:ReportKey", "0123456789012345678901234567890" },
        { "ErrorLogging:Enabled", "yes" }, { "ErrorLogging:Enabled", "1" }, { "ErrorLogging:Enabled", "0" }, { "ErrorLogging:Enabled", "maybe" },
        { "ErrorLogging:Enabled", "truee" }, { "ErrorLogging:Enabled", "on" },
    };

    [Theory]
    [MemberData(nameof(RejectedValues))]
    public void An_invalid_value_fails_with_an_InvalidOperationException_naming_the_key_and_never_the_report_key(string key, string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => ErrorLoggingOptionsValidator.GetValidatedOptions(Config((key, value))));

        Assert.Contains(key, failure.Message);
        if (key.EndsWith("ReportKey", StringComparison.Ordinal))
        {
            Assert.DoesNotContain(value, failure.Message);
            Assert.DoesNotContain(value, failure.ToString());
        }
    }

    [Fact]
    public void The_first_invalid_setting_is_reported_even_when_the_others_are_fine()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => ErrorLoggingOptionsValidator.GetValidatedOptions(Config(
            ("ErrorLogging:Enabled", "true"), ("ErrorLogging:RetentionDays", "30"), ("ErrorLogging:PersistTimeoutSeconds", "100"))));

        Assert.Contains("ErrorLogging:PersistTimeoutSeconds", failure.Message);
    }

    // ---- the shipped settings ----

    [Fact]
    public void The_shipped_appsettings_hold_the_defaults_and_no_report_key()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Conversia.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        foreach (var file in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            var path = Path.Combine(directory!.FullName, "src", "Conversia.API", file);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var hasSection = document.RootElement.TryGetProperty("ErrorLogging", out var section);

            if (file == "appsettings.json")
            {
                Assert.True(hasSection, "appsettings.json must ship the ErrorLogging defaults.");
                Assert.True(section.GetProperty("Enabled").GetBoolean());
                Assert.Equal(90, section.GetProperty("RetentionDays").GetInt32());
                Assert.Equal(20, section.GetProperty("MaxPerFingerprintPerMinute").GetInt32());
                Assert.Equal(3, section.GetProperty("PersistTimeoutSeconds").GetInt32());
                Assert.Equal(60, section.GetProperty("ReportRateLimitPerMinute").GetInt32());
            }

            if (hasSection)
            {
                Assert.True(
                    !section.TryGetProperty("ReportKey", out var key) || string.IsNullOrEmpty(key.GetString()),
                    $"{file} must not ship a report key.");
            }
        }
    }

    // ---- boot ----

    private sealed class ErrorLoggingFactory(IReadOnlyDictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Jwt:SigningKey", TestJwtSettings.SigningKey);
            builder.UseSetting("Webhooks:Processing:Enabled", "false"); // no queue worker: these hosts assert on database traffic and logs
            builder.UseSetting("RateLimiting:Login:PermitLimit", CustomWebApplicationFactory.UnthrottledPermitLimit);
            builder.UseSetting("ConnectionStrings:DefaultConnection", DemoHostSettings.UnreachableConnectionString);
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }

    public static TheoryData<string, string> BootRefusals => new()
    {
        { "ErrorLogging:RetentionDays", "0" }, { "ErrorLogging:RetentionDays", "3651" }, { "ErrorLogging:RetentionDays", "forever" },
        { "ErrorLogging:MaxPerFingerprintPerMinute", "0" }, { "ErrorLogging:MaxPerFingerprintPerMinute", "lots" },
        { "ErrorLogging:PersistTimeoutSeconds", "0" }, { "ErrorLogging:PersistTimeoutSeconds", "31" }, { "ErrorLogging:PersistTimeoutSeconds", "soon" },
        { "ErrorLogging:ReportRateLimitPerMinute", "0" }, { "ErrorLogging:ReportRateLimitPerMinute", "lots" },
        { "ErrorLogging:Enabled", "maybe" }, { "ErrorLogging:Enabled", "1" },
        { "ErrorLogging:ReportKey", "this-key-is-far-too-short" },
    };

    [Theory]
    [MemberData(nameof(BootRefusals))]
    public void The_api_refuses_to_boot_with_an_invalid_setting_and_names_it(string key, string value)
    {
        using var factory = new ErrorLoggingFactory(new Dictionary<string, string> { [key] = value });

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.True(failure is not null, $"The host booted with {key}={value}.");
        var startupError = ExceptionChain.Find<InvalidOperationException>(failure!, e => e.Message.Contains(key));
        Assert.True(startupError is not null, $"Expected an InvalidOperationException naming '{key}'; got: {ExceptionChain.Describe(failure!)}");
        if (key.EndsWith("ReportKey", StringComparison.Ordinal))
        {
            for (var e = failure; e is not null; e = e.InnerException)
            {
                Assert.DoesNotContain(value, e.Message);
                Assert.DoesNotContain(value, e.ToString());
            }
        }
    }

    [Fact]
    public async Task The_api_boots_with_every_boundary_value_and_the_report_route_follows_the_key()
    {
        var withoutKey = new[]
        {
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["ErrorLogging:ReportKey"] = string.Empty },
            new Dictionary<string, string>
            {
                ["ErrorLogging:Enabled"] = "false", ["ErrorLogging:RetentionDays"] = "1", ["ErrorLogging:MaxPerFingerprintPerMinute"] = "1",
                ["ErrorLogging:PersistTimeoutSeconds"] = "1", ["ErrorLogging:ReportRateLimitPerMinute"] = "1",
            },
        };
        var withKey = new Dictionary<string, string>
        {
            ["ErrorLogging:Enabled"] = "true", ["ErrorLogging:RetentionDays"] = "3650", ["ErrorLogging:PersistTimeoutSeconds"] = "30",
            ["ErrorLogging:ReportKey"] = "01234567890123456789012345678901",
        };

        foreach (var settings in withoutKey)
        {
            using var factory = new ErrorLoggingFactory(settings);
            using var client = factory.CreateClient();
            using var response = await client.PostAsync(ErrorTestHost.ReportUrl, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode); // no key: the route is off
        }

        using var keyed = new ErrorLoggingFactory(withKey);
        using var keyedClient = keyed.CreateClient();
        using var keyedResponse = await keyedClient.PostAsync(ErrorTestHost.ReportUrl, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, keyedResponse.StatusCode); // a key was configured: the route is on and wants it
    }
}
