using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.ErrorReporting;

/// <summary>
/// The error code is shown by the shared components, so that a page needs nothing of its own to show it and a new page cannot forget
/// to. The rendering tests prove that for the pages that exist today; this scan of the sources keeps it true tomorrow: no page
/// under <c>Components/Pages</c> mentions the code's test id (<c>error-code</c>) or an <c>ErrorCode</c> of any kind.
/// <para>
/// The one exemption is <c>Error.razor</c>: it is the page the exception handler re-executes (<c>UseExceptionHandler("/Error")</c>),
/// so it is where the code of an unhandled exception of the Web is displayed and may legitimately own that. The base classes
/// (<c>*.cs</c>) are not pages and are not scanned.
/// </para>
/// </summary>
public class ErrorCodeDisplaySourceTests
{
    /// <summary>The page of the exception handler.</summary>
    private const string HandlerPage = "Error.razor";

    private static readonly string[] Mentions = ["error-code", "ErrorCode"];

    private static string Components(params string[] folder) => Path.Combine([RepoPaths.WebProject(), "Components", .. folder]);

    private static IEnumerable<string> RazorFiles(params string[] folder)
        => Directory.EnumerateFiles(Components(folder), "*.razor", SearchOption.AllDirectories);

    [Fact]
    public void No_page_shows_the_error_code_by_itself()
    {
        var pages = RazorFiles("Pages").Where(file => Path.GetFileName(file) != HandlerPage).ToList();
        Assert.True(pages.Count >= 5, "The scan found almost no pages: is it looking in the right place?");

        var offenders = pages
            .Where(file => Mentions.Any(mention => File.ReadAllText(file).Contains(mention, StringComparison.Ordinal)))
            .Select(file => Path.GetRelativePath(RepoPaths.Root(), file))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These pages show or handle the error code themselves; it belongs in the shared components: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_code_is_displayed_by_a_shared_component()
    {
        var shared = RazorFiles("Shared").Concat(RazorFiles("Layout"));

        Assert.Contains(shared, file => File.ReadAllText(file).Contains("error-code", StringComparison.Ordinal));
    }
}
