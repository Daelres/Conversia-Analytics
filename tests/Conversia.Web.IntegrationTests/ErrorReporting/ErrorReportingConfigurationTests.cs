using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.ErrorReporting;

/// <summary>
/// <c>ErrorReporting:Enabled</c>, <c>:ApiKey</c>, <c>:TimeoutSeconds</c> and <c>:MaxPerFingerprintPerMinute</c> of the Web are validated
/// at startup, like the other settings: a bad value stops the Web with an <see cref="InvalidOperationException"/> (or an
/// <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>) naming the key, and the shared secret is never printed.
/// A blank value means "use the default"; without a key the Web still runs (it shows and logs the codes, it just does not report
/// them). None of these tests needs a backend: the hosts have none.
/// <para>
/// A refusal must happen <b>at or before host start</b>: the tests boot the host and make a client, and nothing else, so a setting that
/// is only checked when the first error is reported would boot fine and fail these tests. A refusal that is raised while the host
/// starts can be replaced, by a race in the host's own teardown, with an <see cref="ObjectDisposedException"/>; <c>Boot</c> repeats
/// such a boot (at most three times) before it believes it.
/// </para>
/// </summary>
public class ErrorReportingConfigurationTests
{
    private const string Enabled = "ErrorReporting:Enabled";
    private const string ApiKey = "ErrorReporting:ApiKey";
    private const string Timeout = "ErrorReporting:TimeoutSeconds";
    private const string Budget = "ErrorReporting:MaxPerFingerprintPerMinute";

    private static string KeyOfLength(int length) => ("zQ9-" + new string('x', Math.Max(0, length - 4)))[..length];

    private const int BootAttempts = 3;

    /// <summary>Boots the Web with these settings and returns what stopped it (null when it started).</summary>
    private static Exception? Boot(params (string Key, string? Value)[] settings)
    {
        Exception? failure = null;
        for (var attempt = 1; attempt <= BootAttempts; attempt++)
        {
            using var host = WebTestHost.CreateWithoutApi(WebSettings.With(settings));
            failure = Record.Exception(() => host.NewSession());
            if (failure is null || !ExceptionChain.IsDisposalNoise(failure))
            {
                return failure;
            }
        }

        return failure;
    }

    private static void AssertRefusedNaming(Exception? failure, string setting, string context)
    {
        Assert.True(failure is not null, $"The Web booted although it must refuse to ({context}).");
        var refusal = ExceptionChain.FindRefusal(failure!, setting);
        Assert.True(refusal is not null, $"Expected an InvalidOperationException or OptionsValidationException naming '{setting}' ({context}); got: {ExceptionChain.Describe(failure!)}");
    }

    private static void AssertBoots(Exception? failure, string context)
        => Assert.True(failure is null, $"The Web must boot ({context}) but failed: {(failure is null ? string.Empty : ExceptionChain.Describe(failure))}");

    private static void AssertNeverPrinted(Exception failure, string secret)
    {
        foreach (var e in ExceptionChain.Flatten(failure))
        {
            Assert.DoesNotContain(secret, e.Message);
            Assert.DoesNotContain(secret, e.ToString());
        }
    }

    // ---- the key ----

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(31)]
    public void A_key_shorter_than_32_characters_is_refused_naming_the_setting_and_never_printed(int length)
    {
        var key = KeyOfLength(length);

        var failure = Boot((ApiKey, key));

        AssertRefusedNaming(failure, ApiKey, $"{length}-character key");
        AssertNeverPrinted(failure!, key);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(512)]
    public void A_key_of_32_characters_or_more_boots(int length)
        => AssertBoots(Boot((ApiKey, KeyOfLength(length))), $"{length}-character key");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_key_the_web_boots_and_simply_does_not_report(string? value)
        => AssertBoots(Boot((ApiKey, value)), $"ErrorReporting:ApiKey = '{value}'");

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    public void With_reporting_disabled_a_short_key_is_not_an_error(string enabled)
        => AssertBoots(Boot((Enabled, enabled), (ApiKey, KeyOfLength(8))), $"Enabled={enabled} with an 8-character key");

    [Fact]
    public void A_short_key_is_refused_when_reporting_is_enabled_explicitly()
    {
        var key = KeyOfLength(10);

        var failure = Boot((Enabled, "true"), (ApiKey, key));

        AssertRefusedNaming(failure, ApiKey, "Enabled=true with a 10-character key");
        AssertNeverPrinted(failure!, key);
    }

    // ---- Enabled ----

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData(null)]
    [InlineData("")]
    public void Enabled_accepts_a_boolean_or_nothing(string? value)
        => AssertBoots(Boot((Enabled, value)), $"Enabled='{value}'");

    [Theory]
    [InlineData("yes")]
    [InlineData("tru")]
    [InlineData("2")]
    [InlineData("true, false")]
    [InlineData("null")]
    public void A_non_boolean_Enabled_is_refused_naming_the_setting(string value)
        => AssertRefusedNaming(Boot((Enabled, value)), Enabled, $"Enabled='{value}'");

    // ---- TimeoutSeconds ----

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("30")]
    [InlineData(null)]
    [InlineData("")]
    public void A_timeout_from_1_to_30_seconds_or_nothing_boots(string? value)
        => AssertBoots(Boot((Timeout, value)), $"TimeoutSeconds='{value}'");

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("31")]
    [InlineData("3600")]
    [InlineData("two")]
    [InlineData("1.5")]
    public void A_timeout_out_of_range_or_not_a_whole_number_is_refused_naming_the_setting(string value)
        => AssertRefusedNaming(Boot((Timeout, value)), Timeout, $"TimeoutSeconds='{value}'");

    // ---- MaxPerFingerprintPerMinute ----

    [Theory]
    [InlineData("1")]
    [InlineData("20")]
    [InlineData("100000")]
    [InlineData(null)]
    [InlineData("")]
    public void A_positive_budget_or_nothing_boots(string? value)
        => AssertBoots(Boot((Budget, value)), $"MaxPerFingerprintPerMinute='{value}'");

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    public void A_budget_below_one_or_not_a_number_is_refused_naming_the_setting(string value)
        => AssertRefusedNaming(Boot((Budget, value)), Budget, $"MaxPerFingerprintPerMinute='{value}'");

    // ---- the secret stays secret ----

    [Fact]
    public void A_refusal_caused_by_another_setting_does_not_print_a_valid_key_either()
    {
        var key = KeyOfLength(48);

        var failure = Boot((ApiKey, key), (Timeout, "0"));

        AssertRefusedNaming(failure, Timeout, "valid key, invalid timeout");
        AssertNeverPrinted(failure!, key);
    }
}
