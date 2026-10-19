namespace Conversia.Web.Services;

/// <summary>
/// Lets a form that shows a one-time secret be processed once. The answer to such a form is not redirected (the secret must not travel in
/// an address nor wait in a cookie), so a reload would send it again and make a second channel or rotate a secret just shown. Each drawn
/// form carries a random id; the first submission claims it, a repeated one is refused. Kept in memory for a few minutes: a restart
/// forgets it, which only means a second submission could go through, the same as before this guard existed.
/// The set is bounded: ids are claimed before authorization is checked, so a signed-in user posting made-up ids must not grow it without
/// limit. Past <see cref="DefaultCapacity"/> live ids the oldest one is forgotten first; ids expire after <see cref="Lifetime"/>.
/// </summary>
public sealed class OneTimeSubmissions(TimeProvider? clock = null, int capacity = OneTimeSubmissions.DefaultCapacity)
{
    public const int DefaultCapacity = 10_000;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly int _capacity = Math.Max(1, capacity);

    // Every id lives the same time, so the order of arrival is also the order of expiry: the front is always the next to go.
    private readonly Queue<(string Id, DateTimeOffset Expires)> _order = new();
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>True the first time an id is seen; false for a repeat. An id that is blank or not one of ours is never accepted.</summary>
    public bool TryClaim(string? id)
    {
        if (id is not { Length: 32 } || !id.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        id = id.ToLowerInvariant();
        var now = _clock.GetUtcNow();

        // Check and claim under one lock: two submissions of the same form racing each other must not both win.
        lock (_gate)
        {
            while (_order.TryPeek(out var oldest) && oldest.Expires <= now)
            {
                _live.Remove(_order.Dequeue().Id);
            }

            if (!_live.Add(id))
            {
                return false;
            }

            _order.Enqueue((id, now + Lifetime));
            while (_live.Count > _capacity)
            {
                _live.Remove(_order.Dequeue().Id);
            }

            return true;
        }
    }
}
