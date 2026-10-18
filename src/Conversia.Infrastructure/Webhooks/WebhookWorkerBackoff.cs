namespace Conversia.Infrastructure.Webhooks;

/// <summary>The waits of the worker loop when its cycle fails (as opposed to the retry back-off of a single event).</summary>
public static class WebhookWorkerBackoff
{
    public static readonly TimeSpan FirstFailureDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxFailureDelay = TimeSpan.FromSeconds(60);

    /// <summary>The wait after the <paramref name="consecutiveFailures"/>-th failed cycle in a row: doubles from 2 s, capped at 60 s.</summary>
    public static TimeSpan FailureDelay(int consecutiveFailures)
    {
        var seconds = FirstFailureDelay.TotalSeconds * Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 20));
        return seconds >= MaxFailureDelay.TotalSeconds ? MaxFailureDelay : TimeSpan.FromSeconds(seconds);
    }
}
