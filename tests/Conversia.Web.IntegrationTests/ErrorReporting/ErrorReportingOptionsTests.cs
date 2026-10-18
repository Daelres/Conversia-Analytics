using Conversia.Web.ErrorReporting;

namespace Conversia.Web.IntegrationTests.ErrorReporting;

/// <summary>
/// The <c>ErrorReporting</c> settings of the Web as a plain object: what they default to when nothing is configured, and the
/// guarantee that the shared secret cannot leak by printing the object. No host, no server.
/// </summary>
public class ErrorReportingOptionsTests
{
    private const string Key = "options-tests-shared-secret-0123456789-abcdef";

    [Fact]
    public void Nothing_configured_means_enabled_without_a_key_a_two_second_timeout_and_twenty_reports_per_defect_and_minute()
    {
        var options = new ErrorReportingOptions();

        Assert.True(options.Enabled);
        Assert.Null(options.ApiKey);
        Assert.Equal(2, options.TimeoutSeconds);
        Assert.Equal(20, options.MaxPerFingerprintPerMinute);
    }

    [Fact]
    public void The_section_is_named_ErrorReporting_and_a_key_needs_at_least_32_characters()
    {
        Assert.Equal("ErrorReporting", ErrorReportingOptions.SectionName);
        Assert.Equal(32, ErrorReportingOptions.MinimumKeyLength);
    }

    [Fact]
    public void Printing_the_options_describes_them_but_never_shows_the_key()
    {
        var options = new ErrorReportingOptions { ApiKey = Key, TimeoutSeconds = 5 };

        var text = options.ToString();

        Assert.Contains("Enabled", text);
        Assert.Contains("TimeoutSeconds", text);
        Assert.DoesNotContain(Key, text);
        Assert.DoesNotContain(Key[..12], text);
    }

    [Fact]
    public void Printing_options_without_a_key_does_not_fail()
    {
        var text = new ErrorReportingOptions().ToString();

        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}
