namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// Every wait on something another party must do (a latch, a task the code under test owes) is bounded by one of these, so that an
/// implementation that never does it fails the test with a <see cref="TimeoutException"/> instead of hanging the run.
/// </summary>
internal static class TestTimeouts
{
    /// <summary>Something that should already have happened or be about to (a cancellation reaching a fake, a signal between two tasks).</summary>
    public static readonly TimeSpan Signal = TimeSpan.FromSeconds(10);

    /// <summary>A whole request or a batch of parallel ones, including the best-effort report's own timeout.</summary>
    public static readonly TimeSpan Operation = TimeSpan.FromSeconds(30);

    /// <summary>How much slower than its configured timeout a bounded wait may be before it counts as "waited too long".</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);
}
