using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Conversia.TestSupport;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>How the Web shows the code of an unexpected error, and the assertions every suite shares about it.</summary>
internal static class ErrorCodeAssertions
{
    public const string Selector = "[data-testid='error-code']";

    public const string Label = "Código de error";

    public const string HeaderName = "X-Error-Id";

    /// <summary>The elements that show a code.</summary>
    public static IReadOnlyList<IElement> Elements(PageResponse page) => page.Select(Selector).ToList();

    public static bool IsShown(PageResponse page) => Elements(page).Count > 0;

    /// <summary>
    /// The code has the registry's shape and its date part is today's (UTC); one day either way is tolerated, so that a test running
    /// across midnight, or a code made just before it, is not a failure.
    /// </summary>
    public static void AssertWellFormed(string? code)
    {
        Assert.False(string.IsNullOrEmpty(code), "Expected an error code but there is none.");
        Assert.Matches(ErrorLimits.CodePattern, code!);
        var date = DateTime.ParseExact(code![4..12], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var days = Math.Abs((date.Date - DateTime.UtcNow.Date).TotalDays);
        Assert.True(days <= 1, $"The code {code} is dated {date:yyyy-MM-dd}; today (UTC) is {DateTime.UtcNow:yyyy-MM-dd}.");
    }

    /// <summary>
    /// Asserts the page shows exactly one code, well formed, in an element of its own that holds nothing but the code (so a person can
    /// select and copy it), announced with the Spanish label "Código de error", and returns it.
    /// </summary>
    public static string Shown(PageResponse page)
    {
        var elements = Elements(page);
        Assert.True(elements.Count == 1, $"Expected exactly one {Selector} on {page.RequestUri.PathAndQuery} (status {page.Status}); found {elements.Count}. Page text: {page.Text}");

        var element = elements[0];
        var code = element.TextContent.Trim();
        AssertWellFormed(code);
        Assert.Empty(element.Children);
        Assert.Null(element.GetAttribute("aria-hidden"));
        Assert.Null(element.Closest("[hidden]"));
        Assert.NotNull(element.Closest("main, [data-page]"));
        Assert.Matches(new Regex($@"{Label}:?\s*{Regex.Escape(code)}", RegexOptions.IgnoreCase), page.Text);
        return code;
    }

    public static void NotShown(PageResponse page)
    {
        Assert.False(IsShown(page), $"{page.RequestUri.PathAndQuery} ({page.Status}) shows an error code although the outcome is a known one: {page.Text}");
        Assert.DoesNotMatch(@"ERR-\d{8}-", page.Body);
    }

    /// <summary>The page of an unhandled Web exception: 500, HTML, the code in the page and in <c>X-Error-Id</c>, which carry the same value.</summary>
    public static string AssertUnhandledPage(PageResponse response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.InternalServerError, $"Expected 500 for {response.RequestUri.PathAndQuery} but got {response.Status}. Page text: {response.Text}");
        Assert.Equal("text/html", response.Header("Content-Type")?.Split(';')[0]);
        var code = Shown(response);
        Assert.Equal(code, response.Header(HeaderName));
        WebFlows.AssertNoStackTrace(response);
        return code;
    }
}

/// <summary>Makes ASP.NET Core start an <see cref="System.Diagnostics.Activity"/> for each request, so that a <c>traceparent</c> header flows into the Web's trace id.</summary>
internal sealed class RequestActivities : IDisposable
{
    private readonly System.Diagnostics.ActivityListener _listener = new()
    {
        ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
        Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
    };

    public RequestActivities() => System.Diagnostics.ActivitySource.AddActivityListener(_listener);

    public void Dispose() => _listener.Dispose();
}
