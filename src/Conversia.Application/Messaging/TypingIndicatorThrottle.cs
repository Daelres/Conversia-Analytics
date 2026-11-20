using System.Collections.Concurrent;

namespace Conversia.Application.Messaging;

/// <summary>
/// At most one typing indicator per conversation every <see cref="Interval"/> (docs/MESSAGING.md, "Read receipts and typing"). WhatsApp
/// shows it for up to 25 seconds, so a second one sooner adds nothing but a request. A singleton of one API instance: with several
/// instances each keeps its own, which at worst sends one more indicator; the page also waits between sends. The slot is taken when the
/// indicator is attempted, sent or not: a failure does not make the next keystroke retry at once. It also keeps the cooldown of failed read
/// receipts (<see cref="ReadFailureCooldown"/>).
/// </summary>
public sealed class TypingIndicatorThrottle(TimeProvider timeProvider)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    /// <summary>After a read receipt that failed, how long the conversation's read receipts wait, so every opening does not retry it.</summary>
    public static readonly TimeSpan ReadFailureCooldown = TimeSpan.FromMinutes(5);

    /// <summary>Whether a read receipt of the conversation failed less than <see cref="ReadFailureCooldown"/> ago.</summary>
    public bool ReadCoolingDown(Guid conversationId)
        => _readFailures.TryGetValue(conversationId, out var at) && timeProvider.GetUtcNow() - at < ReadFailureCooldown;

    /// <summary>Starts the cooldown of the conversation's read receipts.</summary>
    public void ReadFailed(Guid conversationId)
    {
        if (_readFailures.Count > PruneAbove)
        {
            _readFailures.Clear(); // a burst of failures across many conversations only costs a few early retries
        }

        _readFailures[conversationId] = timeProvider.GetUtcNow();
    }

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _readFailures = new();

    /// <summary>Above this many conversations the old entries are dropped, so the map cannot grow without bound.</summary>
    private const int PruneAbove = 10_000;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastSent = new();

    /// <summary>True, and the time is taken, when no indicator was sent for the conversation in the last <see cref="Interval"/>.</summary>
    public bool TryAcquire(Guid conversationId)
    {
        var now = timeProvider.GetUtcNow();
        if (_lastSent.Count > PruneAbove)
        {
            foreach (var (id, at) in _lastSent)
            {
                if (now - at >= Interval)
                {
                    _lastSent.TryRemove(id, out _);
                }
            }
        }

        while (true)
        {
            if (_lastSent.TryAdd(conversationId, now))
            {
                return true;
            }

            if (!_lastSent.TryGetValue(conversationId, out var last))
            {
                continue;
            }

            if (now - last < Interval)
            {
                return false;
            }

            if (_lastSent.TryUpdate(conversationId, now, last))
            {
                return true;
            }
        }
    }
}
