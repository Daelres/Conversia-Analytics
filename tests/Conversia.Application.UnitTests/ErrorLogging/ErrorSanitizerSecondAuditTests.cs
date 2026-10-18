using Conversia.Application.ErrorLogging;

namespace Conversia.Application.UnitTests.ErrorLogging;

/// <summary>Regression tests for the second audit round on <see cref="ErrorSanitizer"/>.</summary>
public class ErrorSanitizerSecondAuditTests
{
    private static string Text(string input) => ErrorSanitizer.SanitizeText(input, 2000) ?? string.Empty;

    // ---- finding 1: a doubled quote inside a quoted value is an escaped quote, not the end ----

    [Theory]
    [InlineData("Password='it''s a secret';x", "Password='***';x", "s a secret")]
    [InlineData("Password=\"say \"\"hi\"\" there\";x", "Password=\"***\";x", "there")]
    [InlineData("Password='a''b''c d';next=1", "Password='***';next=1", "c d")]
    [InlineData("Password='';x", "Password='***';x", "x=")]
    [InlineData("Password='abc'; User='ana'", "Password='***'; User='ana'", "abc")]
    [InlineData("Password=\"abc\"; User=\"ana\"", "Password=\"***\"; User=\"ana\"", "abc")]
    public void A_doubled_quote_inside_a_quoted_value_does_not_close_it(string input, string expected, string mustNotRemain)
    {
        var result = Text(input);

        Assert.DoesNotContain(mustNotRemain, result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Password='it''s a secret';x")]
    [InlineData("Password=\"say \"\"hi\"\" there\";x")]
    public void Doubled_quotes_keep_the_sanitizer_idempotent(string input)
    {
        var once = ErrorSanitizer.SanitizeText(input, 2000);

        Assert.Equal(once, ErrorSanitizer.SanitizeText(once, 2000));
    }

    [Fact]
    public void Doubled_quote_heavy_hostile_input_stays_linear_and_controlled()
    {
        var input = "password='" + string.Concat(Enumerable.Repeat("''\"\" a;", 60_000));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = ErrorSanitizer.SanitizeText(input, 16000);

        stopwatch.Stop();
        Assert.NotNull(result);
        Assert.True(result!.Length <= 16000);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void An_empty_quoted_value_does_not_swallow_the_json_after_it()
        => Assert.Equal("{\"password\":\"***\",\"user\":\"ana\"}", Text("{\"password\":\"\",\"user\":\"ana\"}"));

    // ---- finding 2: JSON escaped more than once ----

    [Theory]
    [InlineData("""{\"password\":\"a b c\"}""", """{\"password\":\"***\"}""")]
    [InlineData("""{\\"password\\":\\"a b c\\"}""", """{\\"password\\":\\"***\\"}""")]
    [InlineData("""{\\\"password\\\":\\\"a b c\\\"}""", """{\\\"password\\\":\\\"***\\\"}""")]
    [InlineData("""{\\\\\"token\\\\\":\\\\\"x y z\\\\\",\\\\\"user\\\\\":\\\\\"ana\\\\\"}""", """{\\\\\"token\\\\\":\\\\\"***\\\\\",\\\\\"user\\\\\":\\\\\"ana\\\\\"}""")]
    public void Json_escaped_any_number_of_times_is_redacted_whole(string input, string expected)
    {
        var result = Text(input);

        Assert.DoesNotContain("b c", result);
        Assert.DoesNotContain("y z", result);
        Assert.Equal(expected, result);
        Assert.Equal(result, Text(result));
    }

    // ---- finding 3: an unquoted value must not swallow the rest of a compact JSON document ----

    [Theory]
    [InlineData("{\"password\":null,\"user\":\"ana\"}", "{\"password\":***,\"user\":\"ana\"}")]
    [InlineData("{\"token\":123,\"user\":\"ana\",\"id\":5}", "{\"token\":***,\"user\":\"ana\",\"id\":5}")]
    [InlineData("{\"msg\":\"password=abc\",\"user\":\"ana\"}", "{\"msg\":\"password=***\",\"user\":\"ana\"}")]
    [InlineData("{\"password\":true}", "{\"password\":***}")]
    [InlineData("[{\"token\":1},{\"token\":2}]", "[{\"token\":***},{\"token\":***}]")]
    [InlineData("{\"secret\":12.5,\"a\":[1,2]}", "{\"secret\":***,\"a\":[1,2]}")]
    [InlineData("{\"a\":[\"password=abc\",\"b\"]}", "{\"a\":[\"password=***\",\"b\"]}")]
    [InlineData("{\\\"password\\\":null,\\\"user\\\":\\\"ana\\\"}", "{\\\"password\\\":***,\\\"user\\\":\\\"ana\\\"}")]
    public void An_unquoted_value_stops_where_the_json_value_ends(string input, string expected)
    {
        var result = Text(input);

        Assert.Equal(expected, result);
        Assert.Equal(result, Text(result));
    }

    [Theory]
    [InlineData("Password=p@ss'word", "Password=***")]
    [InlineData("pwd=abc\\def;", "pwd=***;")]
    [InlineData("password=a,b,c rest", "password=*** rest")]
    [InlineData("Password=p\"ss;x", "Password=***;x")]
    [InlineData("secret=a]b} next", "secret=*** next")]
    public void Plain_unquoted_values_are_still_redacted_whole(string input, string expected)
        => Assert.Equal(expected, Text(input));

    [Theory]
    [InlineData("Password=p@ss'word", "ss'word")]
    [InlineData("pwd=abc\\def;", "def")]
    [InlineData("password=a,b,c rest", "b,c")]
    [InlineData("token=ab\"cd;x", "cd")]
    [InlineData("{\"password\":null,\"user\":\"ana\"}", "null")]
    public void No_secret_survives_an_unquoted_value(string input, string secretPart)
        => Assert.DoesNotContain(secretPart, Text(input));

    // ---- finding 4: key-like names in free text ----

    [Theory]
    [InlineData("AccessKey=abc", "AccessKey=***")]
    [InlineData("privateKey=abc", "privateKey=***")]
    [InlineData("SubscriptionKey=abc", "SubscriptionKey=***")]
    [InlineData("Ocp-Apim-Subscription-Key: abc", "Ocp-Apim-Subscription-Key: ***")]
    [InlineData("subscription-key=abc", "subscription-key=***")]
    [InlineData("access_key=abc", "access_key=***")]
    [InlineData("private-key: abc", "private-key: ***")]
    [InlineData("{\"accessKey\":\"abc\",\"user\":\"ana\"}", "{\"accessKey\":\"***\",\"user\":\"ana\"}")]
    public void Key_style_secret_names_are_redacted_in_free_text(string input, string expected)
        => Assert.Equal(expected, Text(input));

    [Theory]
    [InlineData("keyword=abc")]
    [InlineData("key=1 monkey: donkey")]
    [InlineData("the key: value")]
    [InlineData("accesskeyboard=1 subscriptions=2")]
    [InlineData("Primary key: Id")]
    public void Benign_text_with_key_in_it_is_kept(string input)
        => Assert.Equal(input, Text(input));
}
