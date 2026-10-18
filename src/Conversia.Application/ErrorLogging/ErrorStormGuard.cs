using System.Collections.Concurrent;

namespace Conversia.Application.ErrorLogging;

/// <summary>
/// In-memory sliding window per fingerprint: lets at most <c>maxPerWindow</c> occurrences through per minute.
/// Only an occurrence that is let through counts toward the window.
/// </summary>
public sealed class ErrorStormGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int FirstSweepThreshold = 1024;

    private readonly int _maxPerWindow;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Occurrences> _byFingerprint = new(StringComparer.Ordinal);
    private int _sweepThreshold = FirstSweepThreshold;

    public ErrorStormGuard(int maxPerWindow, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerWindow, 1);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _maxPerWindow = maxPerWindow;
        _timeProvider = timeProvider;
    }

    /// <summary>True when this occurrence may be persisted (and is counted); false when the fingerprint is over its budget.</summary>
    public bool TryAcquire(string fingerprint)
    {
        var allowed = TryAcquireIn(fingerprint ?? string.Empty);
        SweepWhenLarge();
        return allowed;
    }

    private bool TryAcquireIn(string fingerprint)
    {
        while (true)
        {
            var occurrences = _byFingerprint.GetOrAdd(fingerprint, static _ => new Occurrences());
            lock (occurrences)
            {
                if (occurrences.Removed)
                {
                    continue;
                }

                var now = _timeProvider.GetTimestamp();
                occurrences.DropExpired(now, _timeProvider);
                if (occurrences.Count >= _maxPerWindow)
                {
                    return false;
                }

                occurrences.Add(now);
                return true;
            }
        }
    }

    // Keeps memory bounded when many distinct defects pass through: forgets fingerprints whose window is empty.
    private void SweepWhenLarge()
    {
        if (_byFingerprint.Count < Volatile.Read(ref _sweepThreshold))
        {
            return;
        }

        var now = _timeProvider.GetTimestamp();
        foreach (var (fingerprint, occurrences) in _byFingerprint)
        {
            lock (occurrences)
            {
                occurrences.DropExpired(now, _timeProvider);
                if (occurrences.Count == 0)
                {
                    occurrences.Removed = true;
                    _byFingerprint.TryRemove(fingerprint, out _);
                }
            }
        }

        Volatile.Write(ref _sweepThreshold, Math.Max(FirstSweepThreshold, _byFingerprint.Count * 2));
    }

    private sealed class Occurrences
    {
        private readonly Queue<long> _timestamps = new();

        public bool Removed { get; set; }

        public int Count => _timestamps.Count;

        public void Add(long timestamp) => _timestamps.Enqueue(timestamp);

        public void DropExpired(long now, TimeProvider timeProvider)
        {
            while (_timestamps.TryPeek(out var oldest) && timeProvider.GetElapsedTime(oldest, now) >= Window)
            {
                _timestamps.Dequeue();
            }
        }
    }
}
