using System.Threading.RateLimiting;
using Conversia.API.Configuration;

namespace Conversia.API.RateLimiting;

/// <summary>
/// The two limits of the webhook, both fixed one-minute windows without queueing:
/// <list type="bullet">
/// <item><b>Unauthenticated junk, per client IP</b>: only <i>failed</i> authentications count. An IP that used its budget is refused (429)
/// before it is even authenticated, so guessing keys and secrets, or flooding with junk, is throttled without ever costing a legitimate
/// provider anything.</item>
/// <item><b>Authenticated deliveries, per channel</b>: generous, counted after the secret matched. One channel flooding (a bug, a loop at
/// the provider) cannot use up the budget of the others, and nobody behind the same proxy IP competes for it.</item>
/// </list>
/// Partitions exist only for IPs that failed (or are being checked) and for real channels, and idle ones are evicted by the limiter itself,
/// so memory stays bounded. Without forwarded-header support (off by default) every caller behind a proxy shares the IP of the proxy; see docs/WEBHOOKS.md.
/// </summary>
public sealed class WebhookRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _failedByIp;
    private readonly PartitionedRateLimiter<Guid> _byChannel;

    public WebhookRateLimiter(WebhookEndpointOptions options)
    {
        _failedByIp = PartitionedRateLimiter.Create<string, string>(ip => RateLimitPartition.GetFixedWindowLimiter(ip, _ => Window(options.FailedAuthRateLimitPerMinute)));
        _byChannel = PartitionedRateLimiter.Create<Guid, Guid>(channel => RateLimitPartition.GetFixedWindowLimiter(channel, _ => Window(options.ChannelRateLimitPerMinute)));
    }

    /// <summary>Seconds to wait when the IP used its budget of failed authentications; null when it may still try.</summary>
    public int? RetryAfterIfBlocked(string clientIp)
    {
        using var lease = _failedByIp.AttemptAcquire(clientIp, permitCount: 0); // asks without spending
        return lease.IsAcquired ? null : RetryAfter(lease);
    }

    /// <summary>Counts a failed authentication of the IP.</summary>
    public void RegisterFailedAuthentication(string clientIp) => _failedByIp.AttemptAcquire(clientIp).Dispose();

    /// <summary>Counts an authenticated delivery of the channel; the seconds to wait when the channel is over its budget, otherwise null.</summary>
    public int? RetryAfterIfChannelOverLimit(Guid channelId)
    {
        using var lease = _byChannel.AttemptAcquire(channelId);
        return lease.IsAcquired ? null : RetryAfter(lease);
    }

    public void Dispose()
    {
        _failedByIp.Dispose();
        _byChannel.Dispose();
    }

    private static FixedWindowRateLimiterOptions Window(int permits)
        => new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 };

    private static int RetryAfter(RateLimitLease lease)
        => lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) : 60;
}
