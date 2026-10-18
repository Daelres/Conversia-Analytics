using System.Diagnostics;
using System.Text;
using Conversia.Application.ErrorLogging;

namespace Conversia.Application.UnitTests.ErrorLogging;

/// <summary>
/// <see cref="ErrorSanitizer"/> is applied server-side to everything the error registry stores, including text the Web
/// supplies. It must remove secrets, leave benign text alone, be idempotent and stay safe on hostile input.
/// </summary>
public class ErrorSanitizerTests
{
    private const string Secret = "S3cr3t-Value-42";

    // A real HS256 token shape: three base64url segments, the first two starting with "eyJ".
    private const string Jwt =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    private static string Text(string? input, int max = 2000) => ErrorSanitizer.SanitizeText(input, max) ?? string.Empty;

    private static string Query(string? input, int max = 1000) => ErrorSanitizer.SanitizeQuery(input, max) ?? string.Empty;

    private static void AssertRedacted(string result)
        => Assert.True(result.Contains("***") || result.Contains("[jwt]"), $"Nothing was redacted: {result}");

    // ---- benign text is left alone ----

    public static TheoryData<string> BenignTexts => new()
    {
        "Ocurrió un error inesperado. Inténtalo de nuevo más tarde.",
        "Object reference not set to an instance of an object.",
        "The password must contain at least 12 characters.",
        "Token expired",
        "A key was not found in the dictionary: 'abc'",
        "La contraseña es incorrecta",
        "Se agotó el tiempo de espera: 30 segundos",
        "Retry-After: 30",
        "Content-Type: application/json; charset=utf-8",
        "Cache-Control: no-store",
        "Host: localhost:5000",
        "Operation took 12:30:45 to finish",
        "2026-10-18T13:45:12.0000000Z",
        "GET /api/products/3f2a9c1e-6b1d-4c1a-9a43-7b0f6b9a1d11 returned 500",
        "status: failed; retry later",
        "   at Conversia.Application.Services.ProductService.UpdateAsync(Guid id, UpdateProductRequest request) in /src/Services/ProductService.cs:line 42",
        "   at System.Threading.Tasks.Task.ThrowIfExceptional(Boolean includeTaskCanceledExceptions)",
        "System.InvalidOperationException: Sequence contains no elements\n   at System.Linq.ThrowHelper.ThrowNoElementsException()\n   at Conversia.Application.Services.ProductService.GetAsync(Guid id)",
        "https://example.test/path?page=2&search=tornillo",
        "user@example.test could not be found",
        "",
    };

    [Theory]
    [MemberData(nameof(BenignTexts))]
    public void Benign_text_is_returned_unchanged(string text)
        => Assert.Equal(text, ErrorSanitizer.SanitizeText(text, 2000) ?? string.Empty);

    [Fact]
    public void Null_text_is_handled_without_throwing()
    {
        var failure = Record.Exception(() => ErrorSanitizer.SanitizeText(null, 100));

        Assert.Null(failure);
        Assert.True(string.IsNullOrEmpty(ErrorSanitizer.SanitizeText(null, 100)));
        Assert.True(string.IsNullOrEmpty(ErrorSanitizer.SanitizeQuery(null, 100)));
    }

    // ---- key/value secrets ----

    public static TheoryData<string> SensitiveKeys => new()
    {
        "password", "pwd", "passwd", "secret", "apikey", "api-key", "api_key", "token", "access_token",
        "refresh_token", "authorization", "signingkey", "client_secret", "connectionstring",
    };

    [Theory]
    [MemberData(nameof(SensitiveKeys))]
    public void The_value_after_a_sensitive_key_and_equals_is_redacted_and_the_rest_of_the_text_is_kept(string key)
    {
        var result = Text($"before {key}={Secret} after");

        Assert.DoesNotContain(Secret, result);
        Assert.Contains("***", result);
        Assert.Contains(key, result, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("before ", result);
        Assert.EndsWith(" after", result);
    }

    [Theory]
    [MemberData(nameof(SensitiveKeys))]
    public void The_value_after_a_sensitive_key_and_a_colon_is_redacted_with_or_without_a_space(string key)
    {
        foreach (var separator in new[] { ":", ": " })
        {
            var result = Text($"before {key}{separator}{Secret} after");

            Assert.DoesNotContain(Secret, result);
            Assert.Contains("***", result);
            Assert.Contains(key, result, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("before ", result);
            Assert.EndsWith(" after", result);
        }
    }

    [Theory]
    [MemberData(nameof(SensitiveKeys))]
    public void Sensitive_keys_are_matched_case_insensitively(string key)
    {
        var shouted = key.ToUpperInvariant();
        var mixed = string.Concat(key.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : c));

        foreach (var variant in new[] { shouted, mixed })
        {
            var result = Text($"{variant}={Secret}");

            Assert.DoesNotContain(Secret, result);
            Assert.Contains("***", result);
        }
    }

    [Fact]
    public void A_value_ends_at_a_semicolon_a_quote_whitespace_or_the_end_of_the_text()
    {
        Assert.Equal("Password=***;", Text($"Password={Secret};"));
        Assert.Equal("Password=*** rest", Text($"Password={Secret} rest"));
        Assert.Equal("end Password=***", Text($"end Password={Secret}"));
        Assert.Equal("token=***\nnext line", Text($"token={Secret}\nnext line"));

        var quoted = Text($"{{\"password\":\"{Secret}\",\"user\":\"ana\"}}");
        Assert.DoesNotContain(Secret, quoted);
        Assert.Contains("\"user\":\"ana\"", quoted);

        var spaced = Text($"{{\"password\": \"{Secret}\", \"user\": \"ana\"}}");
        Assert.DoesNotContain(Secret, spaced);
        Assert.Contains("\"user\": \"ana\"", spaced);

        var singleQuoted = Text($"password='{Secret}' next");
        Assert.DoesNotContain(Secret, singleQuoted);
        Assert.EndsWith("next", singleQuoted);
    }

    [Fact]
    public void Several_secrets_in_one_text_are_all_removed_and_the_text_around_them_survives()
    {
        var input = $"Falló la llamada: Server=db.internal;Password={Secret};User Id=svc_account; token={Secret}2 "
            + $"y Authorization: Bearer {Secret}3 con {Jwt} (apikey={Secret}4) fin";

        var result = Text(input);

        foreach (var leaked in new[] { Secret, "svc_account", Jwt, "eyJhbGci" })
        {
            Assert.DoesNotContain(leaked, result);
        }

        Assert.StartsWith("Falló la llamada:", result);
        Assert.EndsWith("fin", result);
        AssertRedacted(result);
    }

    [Fact]
    public void A_realistic_SqlException_message_loses_its_credentials_but_keeps_the_diagnostic_text()
    {
        const string message =
            "Microsoft.Data.SqlClient.SqlException (0x80131904): A network-related or instance-specific error occurred while establishing a connection to SQL Server. "
            + "The server was not found or was not accessible. (provider: TCP Provider, error: 40 - Could not open a connection to SQL Server) "
            + "Connection string: Server=tcp:prod-sql.internal,1433;Initial Catalog=Conversia;User ID=app_user;Password=Pr0d-S3cret!;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;";

        var result = Text(message);

        Assert.DoesNotContain("Pr0d-S3cret!", result);
        Assert.DoesNotContain("app_user", result);
        Assert.Contains("A network-related or instance-specific error occurred", result);
        Assert.Contains("Could not open a connection to SQL Server", result);
        Assert.Contains("(0x80131904)", result);
        AssertRedacted(result);
    }

    [Theory]
    [InlineData("Password=hunter2;")]
    [InlineData("password=hunter2")]
    [InlineData("PWD=hunter2;Encrypt=True")]
    [InlineData("Server=db;Database=x;User Id=sa;Password=hunter2;")]
    [InlineData("Server=db;user id=sa;pwd=hunter2;")]
    [InlineData("Data Source=db;User ID=sa;Password=hunter2")]
    [InlineData("ConnectionString=Server=db;Password=hunter2;")]
    public void Connection_string_credentials_are_redacted(string text)
    {
        var result = Text(text);

        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("User Id=sa", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("User ID=sa", result, StringComparison.OrdinalIgnoreCase);
        AssertRedacted(result);
    }

    [Fact]
    public void A_user_id_in_a_connection_string_is_redacted_whatever_its_case_and_spacing()
    {
        foreach (var key in new[] { "User Id", "user id", "USER ID", "User ID" })
        {
            var result = Text($"Server=db;{key}=svc_reporting;Password=x;Encrypt=True");

            Assert.DoesNotContain("svc_reporting", result);
            Assert.Contains("Encrypt=True", result);
        }
    }

    // ---- Bearer and JWT ----

    [Theory]
    [InlineData("Bearer")]
    [InlineData("bearer")]
    [InlineData("BEARER")]
    public void A_bearer_token_is_redacted_and_the_text_after_it_is_kept(string scheme)
    {
        var result = Text($"The request failed with {scheme} abc123xyz789 for the current user");

        Assert.DoesNotContain("abc123xyz789", result);
        Assert.EndsWith("for the current user", result);
        AssertRedacted(result);
    }

    [Fact]
    public void An_authorization_header_with_a_bearer_token_leaks_neither_the_scheme_value_nor_the_token()
    {
        foreach (var header in new[] { "Authorization: Bearer abc123xyz789", "authorization=Bearer abc123xyz789", $"Authorization: Bearer {Jwt}" })
        {
            var result = Text($"headers: {header} end");

            Assert.DoesNotContain("abc123xyz789", result);
            Assert.DoesNotContain("eyJ", result);
            Assert.EndsWith("end", result);
            AssertRedacted(result);
        }
    }

    [Fact]
    public void A_jwt_shaped_string_becomes_the_jwt_marker()
    {
        var result = Text($"Invalid credentials {Jwt} at the gateway");

        Assert.Equal("Invalid credentials [jwt] at the gateway", result);
    }

    [Fact]
    public void Every_jwt_in_a_text_is_replaced_even_when_it_is_the_whole_value_of_a_field_or_sits_inside_quotes()
    {
        var result = Text($"first {Jwt} second \"{Jwt}\" third ({Jwt}) end");

        Assert.DoesNotContain("eyJ", result);
        Assert.DoesNotContain("SflKxw", result);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(result, @"\[jwt\]").Count);
        Assert.StartsWith("first ", result);
        Assert.EndsWith(" end", result);
    }

    // ---- control characters ----

    [Fact]
    public void Control_characters_are_stripped()
    {
        Assert.Equal("abcde", Text("a\0b\u0007c\u001bd\u007fe"));
        Assert.Equal("ab", Text("a\u0000\u0001\u0002\u0003\u0004\u0005\u0006b"));
    }

    [Fact]
    public void Line_breaks_and_tabs_survive_so_a_stack_trace_stays_readable()
    {
        // The registry stores the full exception.ToString(); flattening its lines would defeat it.
        var trace = "System.Exception: boom\r\n\tat A.B()\n   at C.D()";

        Assert.Equal(trace, Text(trace));
    }

    [Fact]
    public void Spanish_accents_symbols_and_emoji_are_not_treated_as_control_characters()
    {
        const string text = "Ocurrió un error: ñandú, ¿qué pasó? 100 % — «listo» 😀";

        Assert.Equal(text, Text(text));
    }

    // ---- truncation ----

    [Fact]
    public void A_text_within_the_limit_is_untouched_and_one_over_it_is_cut_with_the_visible_marker()
    {
        var exact = new string('a', 2000);
        Assert.Equal(exact, Text(exact));

        var over = Text(new string('a', 2001));
        Assert.True(over.Length <= 2000, $"Length {over.Length}");
        Assert.EndsWith(ErrorSanitizer.TruncationMarker, over);
        Assert.StartsWith(new string('a', 1900), over);
    }

    [Fact]
    public void A_very_long_text_is_cut_to_the_limit_for_every_field_size_the_table_uses()
    {
        foreach (var limit in new[] { 50, 100, 300, 500, 1000, 2000, 16000 })
        {
            var result = Text(new string('x', limit * 3), limit);

            Assert.True(result.Length <= limit, $"limit {limit}, got {result.Length}");
            Assert.EndsWith(ErrorSanitizer.TruncationMarker, result);
        }
    }

    [Fact]
    public void A_limit_smaller_than_the_marker_still_produces_a_controlled_result()
    {
        foreach (var limit in new[] { 0, 1, 3, ErrorSanitizer.TruncationMarker.Length - 1 })
        {
            var failure = Record.Exception(() => ErrorSanitizer.SanitizeText(new string('x', 100), limit));

            Assert.Null(failure);
            Assert.True((ErrorSanitizer.SanitizeText(new string('x', 100), limit) ?? string.Empty).Length <= limit, $"limit {limit}");
        }
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 3000));

        foreach (var limit in new[] { 99, 100, 101, 1999, 2000 })
        {
            var result = Text(emoji, limit);

            Assert.True(result.Length <= limit);
            AssertValidUtf16(result);
        }
    }

    [Fact]
    public void A_secret_that_straddles_the_cut_is_never_left_half_visible()
    {
        for (var prefixLength = 150; prefixLength <= 200; prefixLength++)
        {
            var result = Text(new string('x', prefixLength) + " Password=hunter2secret and more", 200);

            Assert.True(result.Length <= 200);
            Assert.DoesNotContain("hun", result);
        }
    }

    [Fact]
    public void Secrets_deep_inside_a_long_stack_trace_are_redacted_and_the_trace_is_cut_with_the_marker()
    {
        var trace = new StringBuilder("System.InvalidOperationException: Fallo al abrir la conexión\n");
        for (var i = 0; i < 400; i++)
        {
            trace.Append($"   at Conversia.Infrastructure.Repositories.Frame{i}.Run{i}(Guid id) in /src/Repositories/Frame{i}.cs:line {i + 1}\n");
            if (i == 50)
            {
                trace.Append($"   inner: Server=db;User Id=svc_trace;Password={Secret}trace;Encrypt=True\n");
            }

            if (i == 100)
            {
                trace.Append($"   header Authorization: Bearer {Secret}bearer and {Jwt}\n");
            }
        }

        Assert.True(trace.Length > 16000);

        var result = Text(trace.ToString(), 16000);

        Assert.True(result.Length <= 16000);
        Assert.EndsWith(ErrorSanitizer.TruncationMarker, result);
        Assert.StartsWith("System.InvalidOperationException: Fallo al abrir la conexión\n", result);
        Assert.Contains("Frame0.Run0", result);
        Assert.Contains("Frame120.Run120", result);
        foreach (var leaked in new[] { Secret, "svc_trace", "eyJ", "SflKxw" })
        {
            Assert.DoesNotContain(leaked, result);
        }
    }

    // ---- query strings ----

    [Theory]
    [InlineData("?page=2&search=tornillo")]
    [InlineData("page=2&search=tornillo")]
    [InlineData("?search=tornillo%20grande&page=2&pageSize=10")]
    [InlineData("?flag&other=1")]
    [InlineData("?keyword=abc&codec=h264&monkey=1&sigma=2&tokenizer=x&passwordless=true&secretary=ana")]
    [InlineData("?sku=ABC-1&name=Yunque+de+50+kg")]
    public void A_query_with_benign_keys_and_values_is_unchanged(string query)
        => Assert.Equal(query, Query(query));

    // The requirement's 14 sensitive keys, no more: an implementation may redact further keys, the tests do not demand it.
    [Theory]
    [InlineData("password")]
    [InlineData("pwd")]
    [InlineData("passwd")]
    [InlineData("secret")]
    [InlineData("apikey")]
    [InlineData("api-key")]
    [InlineData("api_key")]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("authorization")]
    [InlineData("signingkey")]
    [InlineData("client_secret")]
    [InlineData("connectionstring")]
    public void The_value_of_a_sensitive_query_key_is_redacted_and_the_neighbours_are_kept(string key)
    {
        foreach (var variant in new[] { key, key.ToUpperInvariant() })
        {
            var result = Query($"?first=1&{variant}={Secret}&last=2");

            Assert.DoesNotContain(Secret, result);
            Assert.Contains("first=1", result);
            Assert.Contains("last=2", result);
            Assert.Contains($"{variant}=***", result);
        }
    }

    [Fact]
    public void Redaction_in_a_query_handles_repeats_encoded_values_empty_values_and_the_last_position()
    {
        var result = Query($"?token={Secret}&page=2&token={Secret}b&client_secret=a%20b%3Dc&password=&search=x&connectionstring={Secret}c");

        Assert.DoesNotContain(Secret, result);
        Assert.DoesNotContain("a%20b", result);
        Assert.Contains("page=2", result);
        Assert.Contains("search=x", result);
        Assert.StartsWith("?", result);
    }

    [Fact]
    public void A_long_query_is_cut_to_the_limit_with_the_marker_and_keeps_redacting_before_the_cut()
    {
        var query = $"?password={Secret}&" + string.Join("&", Enumerable.Range(0, 400).Select(i => $"p{i}=value{i}"));

        var result = Query(query, 1000);

        Assert.True(result.Length <= 1000, $"Length {result.Length}");
        Assert.EndsWith(ErrorSanitizer.TruncationMarker, result);
        Assert.DoesNotContain(Secret, result);
        Assert.Contains("p0=value0", result);
    }

    [Fact]
    public void Control_characters_in_a_query_are_stripped()
    {
        var result = Query("?a=1\u0000&b=\u00072\r\n&c=3");

        Assert.DoesNotContain(result, char.IsControl);
        Assert.Contains("a=1", result);
        Assert.Contains("c=3", result);
    }

    // ---- idempotence ----

    public static TheoryData<string> IdempotenceCorpus => new()
    {
        "Falló: Password=hunter2; Server=db;User Id=sa",
        "Authorization: Bearer abc123xyz789 y token=zzz",
        $"JWT {Jwt} fin",
        "benign text, nothing to hide: 12:30",
        "a\0b\u0007c control chars\u001b",
        "Password=***; Bearer *** [jwt]",
        "key=value code=500",
        "{\"password\":\"hunter2\",\"user\":\"ana\"}",
    };

    [Theory]
    [MemberData(nameof(IdempotenceCorpus))]
    public void Sanitizing_twice_gives_the_same_result_as_sanitizing_once(string text)
    {
        foreach (var limit in new[] { 60, 2000 })
        {
            var once = ErrorSanitizer.SanitizeText(text, limit);
            var twice = ErrorSanitizer.SanitizeText(once, limit);

            Assert.Equal(once, twice);
        }
    }

    [Fact]
    public void Sanitizing_a_truncated_text_again_does_not_cut_it_a_second_time()
    {
        var once = Text(new string('a', 5000) + " Password=hunter2", 300);
        var twice = Text(once, 300);

        Assert.Equal(once, twice);
        Assert.Equal(1, CountOccurrences(twice, ErrorSanitizer.TruncationMarker));
    }

    [Fact]
    public void A_query_sanitized_twice_is_the_same_as_sanitized_once()
    {
        var query = $"?token={Secret}&page=2&code=abc&flag&search=a%20b";

        var once = Query(query);

        Assert.Equal(once, Query(once));
        Assert.Equal(Query(query, 40), Query(Query(query, 40), 40));
    }

    // ---- hostile input stays cheap and controlled ----

    public static TheoryData<string, string> HostileInputs => new()
    {
        { "eyJ" + string.Concat(Enumerable.Repeat(".a", 50_000)), "endless JWT-like dots" },
        { "eyJ" + new string('a', 200_000), "single endless JWT-like segment" },
        { string.Concat(Enumerable.Repeat("password", 20_000)), "password repeated without separators" },
        { "password" + new string('=', 100_000), "password followed by endless equals" },
        { "token:" + new string(' ', 200_000) + "x", "token followed by endless spaces" },
        { string.Concat(Enumerable.Repeat("Bearer ", 30_000)), "Bearer repeated" },
        { "Bearer " + new string('a', 300_000), "one endless bearer value" },
        { string.Concat(Enumerable.Repeat("a.", 150_000)), "dot separated noise" },
        { string.Concat(Enumerable.Repeat("User Id=", 20_000)), "User Id repeated" },
        { new string('\0', 500_000), "half a million control characters" },
    };

    [Theory]
    [MemberData(nameof(HostileInputs))]
    public void Hostile_input_is_handled_in_linear_time_and_never_throws(string input, string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        string? result = null;

        var failure = Record.Exception(() => result = ErrorSanitizer.SanitizeText(input, 16000));
        var queryFailure = Record.Exception(() => ErrorSanitizer.SanitizeQuery("?" + input, 1000));

        stopwatch.Stop();
        Assert.Null(failure);
        Assert.Null(queryFailure);
        Assert.NotNull(result);
        Assert.True(result!.Length <= 16000, reason);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"{reason}: took {stopwatch.Elapsed}");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static void AssertValidUtf16(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), $"Lone high surrogate at {i}.");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(text[i]), $"Lone low surrogate at {i}.");
            }
        }
    }
}
