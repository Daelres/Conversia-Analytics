using Microsoft.Extensions.Caching.Memory;

namespace Conversia.Web.Services;

/// <summary>
/// Lets a form that shows a one-time secret be processed once. The answer to such a form is not redirected (the secret must not travel in
/// an address nor wait in a cookie), so a reload would send it again and make a second channel or rotate a secret just shown. Each drawn
/// form carries a random id; the first submission claims it, a repeated one is refused. Kept in memory for a few minutes: a restart
/// forgets it, which only means a second submission could go through, the same as before this guard existed.
/// </summary>
public sealed class OneTimeSubmissions(IMemoryCache cache)
{
    private readonly object _gate = new();

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>True the first time an id is seen; false for a repeat. An id that is blank or not one of ours is never accepted.</summary>
    public bool TryClaim(string? id)
    {
        if (id is not { Length: 32 } || !id.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        // Check and claim under one lock: two submissions of the same form racing each other must not both win.
        lock (_gate)
        {
            if (cache.TryGetValue("onetime:" + id, out _))
            {
                return false;
            }

            cache.Set("onetime:" + id, true, Lifetime);
            return true;
        }
    }
}
