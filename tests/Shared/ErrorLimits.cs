using System.Text.RegularExpressions;

namespace Conversia.TestSupport;

/// <summary>
/// THE contract from the requirement for the error registry's text columns (and, in camelCase, for the matching fields of the
/// report endpoint) and the shape of an error code. Compiled into the test projects that check what is stored or sent (linked
/// file), so the API suite and the Web suite cannot drift apart. It is spelled out here on purpose and never read from the
/// production constants, so a production change cannot silently move the goalposts of the test that guards it.
/// </summary>
internal static class ErrorLimits
{
    public static readonly Regex CodePattern = new(@"^ERR-\d{8}-[0-9A-HJKMNP-TV-Z]{6}$", RegexOptions.CultureInvariant);

    /// <summary>Column name and maximum length of every text column of <c>ErrorLogs</c>.</summary>
    public static readonly IReadOnlyList<(string Column, int Length)> TextColumns = new (string, int)[]
    {
        ("Code", 24), ("Environment", 50), ("ServiceVersion", 50), ("Instance", 100), ("HttpMethod", 10), ("Path", 500), ("Query", 1000),
        ("Endpoint", 300), ("RouteValues", 1000), ("TraceId", 64), ("RequestId", 100), ("UserAgent", 300), ("ExceptionType", 300),
        ("ExceptionMessage", 2000), ("Context", 2000),
    };

    /// <summary><c>ExceptionDetails</c> is <c>nvarchar(max)</c>; the requirement caps the text that is stored in it.</summary>
    public const int ExceptionDetailsCap = 16000;

    /// <summary>The report fields that are refused (400) when longer than their limit, by their JSON (camelCase) name.</summary>
    public static IEnumerable<(string Field, int Limit)> RefusedWhenLongerReportFields
        => TextColumns.Where(c => c.Column != "Code").Select(c => (char.ToLowerInvariant(c.Column[0]) + c.Column[1..], c.Length));
}
