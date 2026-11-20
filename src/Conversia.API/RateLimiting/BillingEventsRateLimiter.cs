using System.Threading.RateLimiting;

namespace Conversia.API.RateLimiting;

/// <summary>
/// The limits of the gateway's events endpoint, both fixed one-minute windows without queueing (docs/BILLING.md, "API"). Every event is
/// verified first (a SHA-256 over at most 64 KB), then:
/// <list type="bullet">
/// <item><b>Forged or stale events, per client address</b>: only failures count, and an address over its budget gets 429. Behind a proxy
/// without forwarded headers every caller shares the proxy's address, so this budget is all anonymous junk can spend.</item>
/// <item><b>Verified events, platform-wide</b>: a generous budget that junk never touches, so it cannot starve the gateway.</item>
/// </list>
/// </summary>
public sealed class BillingEventsRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _failedByIp;
    private readonly RateLimiter _verified;

    public BillingEventsRateLimiter(int failedPerMinute, int verifiedPerMinute)
    {
        _failedByIp = PartitionedRateLimiter.Create<string, string>(ip => RateLimitPartition.GetFixedWindowLimiter(ip, _ => Window(failedPerMinute)));
        _verified = new FixedWindowRateLimiter(Window(verifiedPerMinute));
    }

    /// <summary>Counts a failed event of the address; true when the address is over its budget.</summary>
    public bool RegisterFailure(string clientIp)
    {
        using var lease = _failedByIp.AttemptAcquire(clientIp);
        return !lease.IsAcquired;
    }

    /// <summary>Counts a verified event; false when the platform-wide budget is used up this minute.</summary>
    public bool TryAcceptVerified()
    {
        using var lease = _verified.AttemptAcquire();
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        _failedByIp.Dispose();
        _verified.Dispose();
    }

    private static FixedWindowRateLimiterOptions Window(int permits)
        => new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 };
}
