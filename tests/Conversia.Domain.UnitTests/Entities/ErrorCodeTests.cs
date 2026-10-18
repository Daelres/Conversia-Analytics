using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Conversia.Domain.Common;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>
/// <c>ERR-yyyyMMdd-XXXXXX</c>: the UTC date, then six characters of the Crockford base32 alphabet
/// (<c>0-9 A-H J K M N P-T V-Z</c>, uppercase, never I, L, O or U).
/// </summary>
public class ErrorCodeTests
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly Regex Format = new(@"^ERR-\d{8}-[0-9A-HJKMNP-TV-Z]{6}$", RegexOptions.CultureInvariant);

    private static readonly DateTime Moment = new(2026, 10, 18, 13, 45, 12, DateTimeKind.Utc);

    [Fact]
    public void New_produces_the_documented_format_for_the_given_utc_date()
    {
        var code = ErrorCode.New(Moment);

        Assert.Matches(Format, code);
        Assert.StartsWith("ERR-20261018-", code);
        Assert.Equal(19, code.Length);
    }

    [Theory]
    [InlineData(2026, 1, 1, 0, 0, 0, "20260101")]
    [InlineData(2026, 12, 31, 23, 59, 59, "20261231")]
    [InlineData(2024, 2, 29, 12, 0, 0, "20240229")]
    public void New_uses_the_date_it_is_given_and_not_the_clock_of_the_machine(int year, int month, int day, int hour, int minute, int second, string expectedDate)
    {
        var code = ErrorCode.New(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc));

        Assert.StartsWith($"ERR-{expectedDate}-", code);
        Assert.Matches(Format, code);
    }

    [Fact]
    public void New_handles_the_extremes_of_DateTime_without_throwing()
    {
        Assert.Matches(Format, ErrorCode.New(DateTime.MinValue));
        Assert.Matches(Format, ErrorCode.New(DateTime.MaxValue));
        Assert.StartsWith("ERR-00010101-", ErrorCode.New(DateTime.MinValue));
        Assert.StartsWith("ERR-99991231-", ErrorCode.New(DateTime.MaxValue));
    }

    [Fact]
    public void Every_generated_code_is_valid_and_the_date_never_depends_on_the_current_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // Arabic digits and a different calendar would change yyyyMMdd if the culture leaked in.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            var code = ErrorCode.New(Moment);

            Assert.Matches(Format, code);
            Assert.StartsWith("ERR-20261018-", code);
            Assert.True(ErrorCode.IsValid(code));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Five_thousand_codes_are_well_formed_use_the_whole_alphabet_and_are_almost_all_distinct()
    {
        var codes = Enumerable.Range(0, 5000).Select(_ => ErrorCode.New(Moment)).ToList();

        Assert.All(codes, code => Assert.Matches(Format, code));
        Assert.All(codes, code => Assert.True(ErrorCode.IsValid(code), code));

        var distinct = codes.Distinct(StringComparer.Ordinal).Count();
        Assert.True(distinct >= 4950, $"Only {distinct} of 5000 codes were distinct.");

        var suffixCharacters = codes.SelectMany(c => c[^6..]).ToHashSet();
        Assert.Subset(Alphabet.ToHashSet(), suffixCharacters);
        Assert.Equal(Alphabet.Length, suffixCharacters.Count); // each of the 32 characters shows up in 30 000 draws
    }

    [Fact]
    public void The_letters_I_L_O_and_U_never_appear_in_the_random_part()
    {
        var suffixes = Enumerable.Range(0, 5000).Select(_ => ErrorCode.New(Moment)[^6..]);

        foreach (var forbidden in "ILOUilou")
        {
            Assert.DoesNotContain(suffixes, suffix => suffix.Contains(forbidden));
        }
    }

    [Fact]
    public void Codes_generated_in_parallel_are_valid_and_not_duplicated_by_a_shared_random_seed()
    {
        var codes = new ConcurrentBag<string>();

        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            for (var i = 0; i < 1000; i++)
            {
                codes.Add(ErrorCode.New(Moment));
            }
        });

        Assert.Equal(8000, codes.Count);
        Assert.All(codes, code => Assert.True(ErrorCode.IsValid(code), code));
        var distinct = codes.Distinct(StringComparer.Ordinal).Count();
        Assert.True(distinct >= 7920, $"Only {distinct} of 8000 parallel codes were distinct.");
    }

    [Theory]
    [InlineData("ERR-20261018-ABCDEF")]
    [InlineData("ERR-20261018-012345")]
    [InlineData("ERR-20261018-HJKMNP")]
    [InlineData("ERR-20261018-QRSTVW")]
    [InlineData("ERR-20261018-XYZ789")]
    [InlineData("ERR-20260101-000000")]
    [InlineData("ERR-20261231-ZZZZZZ")]
    public void IsValid_accepts_well_formed_codes(string code) => Assert.True(ErrorCode.IsValid(code));

    public static TheoryData<string?, string> InvalidForms => new()
    {
        { null, "null" },
        { "", "empty" },
        { " ", "blank" },
        { "ERR", "prefix only" },
        { "ERR-20261018", "no suffix" },
        { "ERR-20261018-", "empty suffix" },
        { "ERR-20261018-ABCDE", "suffix one short" },
        { "ERR-20261018-ABCDEFG", "suffix one long" },
        { "ERR-2026101-ABCDEF", "date one short" },
        { "ERR-202610180-ABCDEF", "date one long" },
        { "ERR-2026-10-18-ABCDEF", "date with separators" },
        { "ERR-ABCDEFGH-ABCDEF", "date not digits" },
        { "err-20261018-ABCDEF", "lowercase prefix" },
        { "ERR-20261018-abcdef", "lowercase suffix" },
        { "Err-20261018-ABCDEF", "mixed-case prefix" },
        { "ERR_20261018_ABCDEF", "underscores" },
        { "ERR-20261018ABCDEF", "missing second dash" },
        { "ERR20261018-ABCDEF", "missing first dash" },
        { "XRR-20261018-ABCDEF", "wrong prefix" },
        { "ERR-20261018-ABCDEI", "contains I" },
        { "ERR-20261018-ABCDEL", "contains L" },
        { "ERR-20261018-ABCDEO", "contains O" },
        { "ERR-20261018-ABCDEU", "contains U" },
        { "ERR-20261018-IIIIII", "only I" },
        { "ERR-20261018-ABC DE", "space inside" },
        { "ERR-20261018-ABC-DE", "dash inside the suffix" },
        { " ERR-20261018-ABCDEF", "leading space" },
        { "ERR-20261018-ABCDEF ", "trailing space" },
        { "ERR-20261018-ABCDEF\n", "trailing newline" },
        { "\nERR-20261018-ABCDEF", "leading newline" },
        { "ERR-20261018-ABCDEF\0", "trailing NUL" },
        { "ERR-20261018-ABCDEF-extra", "trailing text" },
        { "ERR-20261018-ABCDEF\nERR-20261018-ABCDEF", "two codes" },
        { "ERR-٢٠٢٦١٠١٨-ABCDEF", "Arabic-Indic digits in the date" },
        { "ERR-２０２６１０１８-ABCDEF", "full-width digits in the date" },
        { "ERR-20261018-ＡＢＣＤＥＦ", "full-width letters" },
        { "ERR-20261018-ÁBCDEF", "accented letter" },
        { "ERR-20261018-ABCDE​", "zero-width space" },
        { "ERR-20261018-'; DROP TABLE ErrorLogs;--", "SQL injection attempt" },
        { new string('A', 5000), "very long text" },
    };

    [Theory]
    [MemberData(nameof(InvalidForms))]
    public void IsValid_rejects_known_invalid_forms_without_throwing(string? value, string reason)
    {
        var failure = Record.Exception(() => ErrorCode.IsValid(value));

        Assert.True(failure is null, $"IsValid threw for {reason}: {failure}");
        Assert.False(ErrorCode.IsValid(value), reason);
    }
}
