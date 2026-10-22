using System.Text;
using Conversia.Application.Ingestion;

namespace Conversia.Application.UnitTests.Ingestion;

public class WebhookBasicAuthTests
{
    private const string Secret = "dummy-secret_for.tests:with/colon+plus=";

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void The_password_of_a_basic_header_is_the_secret()
        => Assert.Equal(Secret, WebhookBasicAuth.TryReadPassword($"Basic {B64($"conversia:{Secret}")}"));

    [Fact]
    public void The_header_built_for_a_secret_reads_back()
        => Assert.Equal(Secret, WebhookBasicAuth.TryReadPassword(WebhookBasicAuth.Header(Secret)));

    [Theory]
    [InlineData("conversia")]
    [InlineData("anything at all")]
    [InlineData("")]
    public void The_user_name_is_ignored_even_when_empty(string user)
        => Assert.Equal("s3cret", WebhookBasicAuth.TryReadPassword($"Basic {B64($"{user}:s3cret")}"));

    [Theory]
    [InlineData("basic")]
    [InlineData("BASIC")]
    [InlineData("bAsIc")]
    public void The_scheme_is_case_insensitive(string scheme)
        => Assert.Equal("s3cret", WebhookBasicAuth.TryReadPassword($"{scheme} {B64("u:s3cret")}"));

    [Fact]
    public void Surrounding_whitespace_of_the_whole_value_is_tolerated()
        => Assert.Equal("s3cret", WebhookBasicAuth.TryReadPassword($"  Basic \t {B64("u:s3cret")} \t"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Basic")]
    [InlineData("Basic ")]
    [InlineData("Bearer dXNlcjpzZWNyZXQ=")]
    [InlineData("BasicdXNlcjpzZWNyZXQ=")]
    [InlineData("dXNlcjpzZWNyZXQ=")]
    [InlineData("Basic not*base64!")]
    [InlineData("Basic dXNlcjpzZWNyZXQ")] // unpadded
    [InlineData("Basic dXNlcjpzZWNyZXQ=,Basic dXNlcjpzZWNyZXQ=")] // two values joined
    public void A_header_that_is_not_a_well_formed_basic_credential_yields_nothing(string? header)
        => Assert.Null(WebhookBasicAuth.TryReadPassword(header));

    [Fact]
    public void A_credential_without_a_colon_or_with_an_empty_password_yields_nothing()
    {
        Assert.Null(WebhookBasicAuth.TryReadPassword($"Basic {B64("conversia-s3cret")}"));
        Assert.Null(WebhookBasicAuth.TryReadPassword($"Basic {B64("conversia:")}"));
        Assert.Null(WebhookBasicAuth.TryReadPassword($"Basic {B64(":")}"));
    }

    [Fact]
    public void Bytes_that_are_not_utf8_yield_nothing()
        => Assert.Null(WebhookBasicAuth.TryReadPassword($"Basic {Convert.ToBase64String([0x75, 0x3a, 0xFF, 0xFE, 0xFD])}"));

    [Fact]
    public void A_header_over_the_length_cap_is_refused_before_decoding_even_if_it_would_be_valid()
    {
        var valid = $"Basic {B64($"u:{new string('a', 1000)}")}";
        Assert.True(valid.Length > WebhookBasicAuth.MaxHeaderLength);
        Assert.Null(WebhookBasicAuth.TryReadPassword(valid));
        Assert.Null(WebhookBasicAuth.TryReadPassword("Basic " + new string('A', 100_000)));
    }

    [Fact]
    public void A_header_just_under_the_cap_is_read()
    {
        var header = $"Basic {B64($"u:{new string('a', 700)}")}";
        Assert.True(header.Length <= WebhookBasicAuth.MaxHeaderLength);
        Assert.Equal(new string('a', 700), WebhookBasicAuth.TryReadPassword(header));
    }
}
