using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Conversations;

/// <summary>
/// The contract of <c>wwwroot/js/inbox.js</c> (review fix 8). The repository has no JavaScript test runner, so these checks read the script
/// for the behaviour the server side relies on; the behaviour in a browser is the manual checklist of docs/MESSAGING.md ("Inbox").
/// </summary>
public class InboxScriptContractTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(RepoPaths.WebProject(), "wwwroot", "js", "inbox.js"));

    [Theory]
    [InlineData("If-None-Match", "asks for the list with the last ETag, so an unchanged inbox is a 304")]
    [InlineData("AbortController", "aborts the requests in flight on a navigation")]
    [InlineData("MAX_BACKOFF_MS = 60000", "backs off on errors, at most a minute")]
    [InlineData("response.status === 401 || response.status === 403", "stops on a session that ended")]
    [InlineData("poll-signed-out", "tells the person to sign in again")]
    [InlineData("document.hidden", "pauses while the tab is hidden")]
    [InlineData("enhancedload", "re-binds after Blazor's enhanced navigation")]
    [InlineData("mine !== generation", "a stale loop stops when a newer generation started")]
    [InlineData("bindOnce(area, 'typing')", "never adds a second typing listener")]
    [InlineData("TYPING_INTERVAL_MS = 20000", "posts typing at most every 20 seconds")]
    [InlineData("data-read-url", "the read receipt is posted by the script, not by the page render")]
    [InlineData("__RequestVerificationToken", "posts carry the antiforgery token")]
    public void The_script_keeps_its_contract(string marker, string why)
        => Assert.True(Script.Contains(marker, StringComparison.Ordinal), $"inbox.js no longer {why} ({marker}).");

    [Fact]
    public void The_script_is_loaded_by_every_page_and_runs_on_the_same_origin_only()
    {
        Assert.DoesNotContain("http://", Script);
        Assert.DoesNotContain("https://", Script);
        Assert.DoesNotContain("eval(", Script);
        Assert.Contains("credentials: 'same-origin'", Script);
    }
}
