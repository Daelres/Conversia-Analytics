using Conversia.Application.ErrorLogging;

namespace Conversia.Application.UnitTests.ErrorLogging;

/// <summary>Regression tests for the findings of the error registry audit on <see cref="ErrorSanitizer"/>.</summary>
public class ErrorSanitizerAuditTests
{
    private static string Text(string input) => ErrorSanitizer.SanitizeText(input, 2000) ?? string.Empty;

    private static string Query(string input) => ErrorSanitizer.SanitizeQuery(input, 1000) ?? string.Empty;

    // ---- finding 1: query keys that merely contain a sensitive word ----

    [Theory]
    [InlineData("newPassword")]
    [InlineData("oldPassword")]
    [InlineData("confirmPassword")]
    [InlineData("passwordConfirmation")]
    [InlineData("accessToken")]
    [InlineData("id_token")]
    [InlineData("idToken")]
    [InlineData("X-Api-Key")]
    [InlineData("clientSecret")]
    [InlineData("client-secret")]
    [InlineData("client.secret")]
    [InlineData("secretKey")]
    [InlineData("privateSigningKey")]
    [InlineData("Authorization")]
    [InlineData("proxy-authorization")]
    [InlineData("db_connectionString")]
    [InlineData("sig")]
    [InlineData("signature")]
    [InlineData("X-Amz-Signature")]
    [InlineData("key")]
    [InlineData("KEY")]
    [InlineData("pwd")]
    [InlineData("userPwd")]
    [InlineData("passwd")]
    public void A_query_key_that_contains_a_sensitive_word_is_redacted(string key)
    {
        var result = Query($"?first=1&{key}=Hunter2&last=2");

        Assert.DoesNotContain("Hunter2", result);
        Assert.Contains($"{key}=***", result);
        Assert.Contains("first=1", result);
        Assert.Contains("last=2", result);
    }

    [Fact]
    public void The_whole_example_of_the_audit_is_redacted_except_the_innocuous_keys()
    {
        var result = Query("?newPassword=Hunter2&accessToken=abc&clientSecret=z&password=ok&id_token=abc&key=abc&code=abc");

        Assert.Equal("?newPassword=***&accessToken=***&clientSecret=***&password=***&id_token=***&key=***&code=abc", result);
    }

    [Fact]
    public void An_encoded_query_key_is_decoded_before_it_is_judged()
    {
        var result = Query("?new%50assword=Hunter2&access%5Ftoken=abc&client+secret=z&api%2Dkey=q");

        Assert.DoesNotContain("Hunter2", result);
        Assert.DoesNotContain("=abc", result);
        Assert.DoesNotContain("=z", result);
        Assert.DoesNotContain("=q", result);
    }

    [Fact]
    public void A_very_long_query_key_cannot_hide_a_sensitive_word()
    {
        var result = Query($"?{new string('a', 200)}Password=Hunter2&page=2");

        Assert.DoesNotContain("Hunter2", result);
        Assert.Contains("page=2", result);
    }

    [Theory]
    [InlineData("?page=2&sort=name&id=7&code=abc&state=xyz&pageSize=10&q=tornillo")]
    [InlineData("?keyword=abc&codec=h264&monkey=1&sigma=2&design=3&signal=4&keys=5&donkey=6")]
    [InlineData("?tokenizer=x&passwordless=true&secretary=ana&userId=7&user=ana")]
    public void Innocuous_query_keys_are_kept(string query)
        => Assert.Equal(query, Query(query));

    // ---- finding 2: secrets with spaces, quotes or backslashes in the free text ----

    [Theory]
    [InlineData("Password=\"my secret pass\";x", "Password=\"***\";x", "secret pass")]
    [InlineData("{\"password\":\"a b c\"}", "{\"password\":\"***\"}", "b c")]
    [InlineData("{\"password\": \"a b c\", \"user\": \"ana\"}", "{\"password\": \"***\", \"user\": \"ana\"}", "b c")]
    [InlineData("pwd=abc\\def;", "pwd=***;", "def")]
    [InlineData("Password=p@ss'word", "Password=***", "ss'word")]
    [InlineData("Password='it is secret';next", "Password='***';next", "is secret")]
    [InlineData("Password=\"has ' quote and ; semicolon\" end", "Password=\"***\" end", "semicolon")]
    [InlineData("secret=\"he said \\\"no\\\"\" end", "secret=\"***\" end", "no")]
    public void A_secret_with_spaces_quotes_or_backslashes_is_redacted_whole(string input, string expected, string mustNotRemain)
    {
        var result = Text(input);

        Assert.DoesNotContain(mustNotRemain, result);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void A_quoted_secret_does_not_swallow_the_text_after_its_closing_quote()
    {
        var result = Text("db: Password=\"my secret pass\"; User=ana; retry later");

        Assert.Equal("db: Password=\"***\"; User=ana; retry later", result);
    }

    [Theory]
    [InlineData("Password=\"my secret pass\";x")]
    [InlineData("{\"password\":\"a b c\"}")]
    [InlineData("pwd=abc\\def;")]
    [InlineData("Password=p@ss'word")]
    public void The_new_value_rules_keep_the_sanitizer_idempotent(string input)
    {
        var once = ErrorSanitizer.SanitizeText(input, 2000);

        Assert.Equal(once, ErrorSanitizer.SanitizeText(once, 2000));
    }

    [Fact]
    public void An_unterminated_quote_still_redacts_the_value()
    {
        var result = Text("Password=\"never closed and more");

        Assert.DoesNotContain("never", result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void Quote_heavy_hostile_input_stays_linear_and_controlled()
    {
        var input = "password=\"" + string.Concat(Enumerable.Repeat("'\"\\ ;", 60_000));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = ErrorSanitizer.SanitizeText(input, 16000);

        stopwatch.Stop();
        Assert.NotNull(result);
        Assert.True(result!.Length <= 16000);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }
}
