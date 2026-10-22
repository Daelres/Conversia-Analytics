using Conversia.Application.Interfaces;

namespace Conversia.Application.Services;

/// <summary>
/// In-memory <see cref="IAdminLoginThrottle"/>: after <see cref="Threshold"/> failures on an account (counted while the account keeps failing;
/// they are forgotten after <see cref="Window"/> without a failure) every further failure doubles the wait, from <see cref="BaseDelay"/> up to
/// <see cref="MaxDelay"/>. Memory is bounded: at most <see cref="MaxAccounts"/> accounts are tracked, expired ones go first and then the
/// one that failed longest ago. Registered as a singleton.
/// </summary>
public sealed class AdminLoginThrottle : IAdminLoginThrottle
{
    public const int Threshold = 5;
    public const int MaxAccounts = 10_000;
    public const int MaxKeyLength = 256;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public AdminLoginThrottle(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>The accounts tracked now (for tests and diagnostics; never the emails).</summary>
    public int TrackedAccounts
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public TimeSpan? RetryAfter(string email)
    {
        var key = KeyOf(email);
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (IsExpired(entry, now))
            {
                _entries.Remove(key);
                return null;
            }

            return entry.LockedUntil > now ? entry.LockedUntil - now : null;
        }
    }

    public void RecordFailure(string email)
    {
        var key = KeyOf(email);
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && IsExpired(entry, now))
            {
                _entries.Remove(key);
                entry = null;
            }

            if (entry is null)
            {
                MakeRoom(now);
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Failures++;
            entry.LastFailureAt = now;
            if (entry.Failures >= Threshold)
            {
                entry.LockedUntil = now + DelayFor(entry.Failures);
            }
        }
    }

    public void Reset(string email)
    {
        lock (_gate)
        {
            _entries.Remove(KeyOf(email));
        }
    }

    /// <summary>The wait after the <paramref name="failures"/>-th failure: the base delay doubled for each failure past the threshold, capped.</summary>
    public static TimeSpan DelayFor(int failures)
    {
        var doublings = Math.Clamp(failures - Threshold, 0, 30);
        var seconds = BaseDelay.TotalSeconds * Math.Pow(2, doublings);
        return seconds >= MaxDelay.TotalSeconds ? MaxDelay : TimeSpan.FromSeconds(seconds);
    }

    private static string KeyOf(string email)
    {
        var key = (email ?? string.Empty).Trim().ToLowerInvariant();
        return key.Length <= MaxKeyLength ? key : key[..MaxKeyLength];
    }

    private static bool IsExpired(Entry entry, DateTime now) => entry.LockedUntil <= now && now - entry.LastFailureAt > Window;

    private void MakeRoom(DateTime now)
    {
        if (_entries.Count < MaxAccounts)
        {
            return;
        }

        foreach (var key in _entries.Where(e => IsExpired(e.Value, now)).Select(e => e.Key).ToList())
        {
            _entries.Remove(key);
        }

        if (_entries.Count >= MaxAccounts)
        {
            _entries.Remove(_entries.MinBy(e => e.Value.LastFailureAt).Key);
        }
    }

    private sealed class Entry
    {
        public int Failures { get; set; }

        public DateTime LastFailureAt { get; set; }

        public DateTime LockedUntil { get; set; }
    }
}
