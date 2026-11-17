namespace Conversia.Domain.Common;

/// <summary>
/// WhatsApp's customer service window (docs/MESSAGING.md, "Customer service window"): free-form messages can be sent to a customer only
/// within 24 hours of the customer's last message, and every message of the customer starts the 24 hours over. Outside it, only an
/// approved template can be sent. Only the customer's messages count: nothing the business sends opens or extends it.
/// </summary>
public static class CustomerServiceWindow
{
    public static readonly TimeSpan Length = TimeSpan.FromHours(24);

    /// <summary>How long before closing an open window is shown as "closing soon".</summary>
    public static readonly TimeSpan ClosingSoonThreshold = TimeSpan.FromHours(2);

    /// <summary>When the window closes; null when the customer never wrote (no window was ever opened).</summary>
    public static DateTime? ClosesAt(DateTime? lastCustomerMessageAt)
        => lastCustomerMessageAt is { } at ? UtcTime.Normalize(at) + Length : null;

    /// <summary>Open from the customer's message up to, not including, 24 hours later.</summary>
    public static bool IsOpen(DateTime? lastCustomerMessageAt, DateTime nowUtc)
        => ClosesAt(lastCustomerMessageAt) is { } closes && nowUtc < closes;

    /// <summary>Open, and closing within <see cref="ClosingSoonThreshold"/>.</summary>
    public static bool IsClosingSoon(DateTime? lastCustomerMessageAt, DateTime nowUtc)
        => IsOpen(lastCustomerMessageAt, nowUtc) && ClosesAt(lastCustomerMessageAt)!.Value - nowUtc <= ClosingSoonThreshold;
}
