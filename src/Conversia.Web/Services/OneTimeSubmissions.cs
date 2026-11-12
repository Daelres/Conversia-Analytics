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
    /// <summary>How many live claims one owner may hold; past it that owner's own oldest claim is forgotten first, and nobody else's.</summary>
    public const int OwnerCapacity = 64;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private sealed class OwnerClaims
    {
        public Queue<(string Key, DateTimeOffset Expires)> Order { get; } = new();

        public HashSet<string> Live { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, OwnerClaims> _owned = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweep;

    /// <summary>How many owners hold a live claim right now.</summary>
    public int Owners
    {
        get
        {
            lock (_gate)
            {
                return _owned.Count;
            }
        }
    }

    /// <summary>
    /// The same guard for a form that belongs to somebody: <paramref name="owner"/> is the signed-in user (taken from the session, never
    /// from the request body) and <paramref name="context"/> what the form acts on (a conversation). Each owner has a budget of their
    /// own, so a user who posts made-up ids can only push out their own claims: another user's sent message stays remembered.
    /// The number of owners is the number of people who submitted such a form in the last <see cref="Lifetime"/>.
    /// </summary>
    public bool TryClaim(string owner, string context, string? id)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(context) || !IsOurs(id))
        {
            return false;
        }

        var key = $"{context}\n{id!.ToLowerInvariant()}";
        var now = _clock.GetUtcNow();

        lock (_gate)
        {
            if (now >= _nextSweep)
            {
                _nextSweep = now + SweepInterval;
                foreach (var stale in _owned.Where(entry => Expire(entry.Value, now) == 0).Select(entry => entry.Key).ToList())
                {
                    _owned.Remove(stale);
                }
            }

            if (!_owned.TryGetValue(owner, out var claims))
            {
                _owned[owner] = claims = new OwnerClaims();
            }

            Expire(claims, now);
            if (!claims.Live.Add(key))
            {
                return false;
            }

            claims.Order.Enqueue((key, now + Lifetime));
            while (claims.Live.Count > OwnerCapacity)
            {
                claims.Live.Remove(claims.Order.Dequeue().Key);
            }

            return true;
        }
    }

    private static int Expire(OwnerClaims claims, DateTimeOffset now)
    {
        while (claims.Order.TryPeek(out var oldest) && oldest.Expires <= now)
        {
            claims.Live.Remove(claims.Order.Dequeue().Key);
        }

        return claims.Live.Count;
    }

    private static bool IsOurs(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigit);

    public bool TryClaim(string? id)
    {
        if (!IsOurs(id))
        {
            return false;
        }

        id = id!.ToLowerInvariant();
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
